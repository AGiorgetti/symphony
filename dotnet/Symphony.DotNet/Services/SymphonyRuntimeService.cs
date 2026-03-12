using System.Threading.Channels;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class SymphonyRuntimeService(
    RuntimeStateStore store,
    WorkflowStore workflowStore,
    TrackerClientFactory trackerClientFactory,
    WorkspaceManager workspaceManager,
    IAgentRunner agentRunner,
    ILogger<SymphonyRuntimeService> logger) : BackgroundService
{
    private const int ContinuationRetryDelayMs = 1_000;
    private const int PollTransitionRenderDelayMs = 20;

    private readonly Channel<IRuntimeMessage> _messages = Channel.CreateUnbounded<IRuntimeMessage>();
    private readonly OrchestratorState _state = new();

    public Task<RefreshPayload> RequestRefreshAsync(CancellationToken cancellationToken = default)
    {
        var reply = new TaskCompletionSource<RefreshPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_messages.Writer.TryWrite(new RefreshRequestMessage(reply)))
        {
            throw new InvalidOperationException("runtime_unavailable");
        }

        return reply.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshot = workflowStore.LoadInitial();
        ValidateStartup(snapshot.Workflow);
        RefreshRuntimeConfig(snapshot.Workflow);

        logger.LogInformation("action=runtime_start outcome=starting workflow_path={WorkflowPath}", snapshot.Workflow.Path);
        await StartupTerminalWorkspaceCleanupAsync(snapshot.Workflow, stoppingToken);
        PublishState(snapshot);
        ScheduleTick(0, stoppingToken);

        await foreach (var message in _messages.Reader.ReadAllAsync(stoppingToken))
        {
            switch (message)
            {
                case TickMessage tick:
                    HandleTick(tick, workflowStore.Current(), stoppingToken);
                    break;
                case RunPollCycleMessage:
                    await HandleRunPollCycleAsync(workflowStore.Current(), stoppingToken);
                    break;
                case WorkerUpdateMessage workerUpdate:
                    HandleWorkerUpdate(workerUpdate, workflowStore.Current());
                    break;
                case WorkerCompletedMessage workerCompleted:
                    HandleWorkerCompleted(workerCompleted, workflowStore.Current(), stoppingToken);
                    break;
                case RetryDueMessage retryDue:
                    await HandleRetryDueAsync(retryDue, workflowStore.Current(), stoppingToken);
                    break;
                case RefreshRequestMessage refreshRequest:
                    HandleRefreshRequest(refreshRequest, stoppingToken);
                    break;
            }
        }
    }

    private void ValidateStartup(WorkflowDocument workflow)
    {
        if (workflow.ValidationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", workflow.ValidationErrors));
        }
    }

    private void RefreshRuntimeConfig(WorkflowDocument workflow)
    {
        _state.PollIntervalMs = workflow.Polling.IntervalMs;
        _state.MaxConcurrentAgents = workflow.Agent.MaxConcurrentAgents;
    }

    private void HandleTick(TickMessage tick, WorkflowStoreSnapshot snapshot, CancellationToken stoppingToken)
    {
        if (!string.Equals(tick.Token, _state.TickToken, StringComparison.Ordinal))
        {
            return;
        }

        RefreshRuntimeConfig(snapshot.Workflow);
        _state.PollCheckInProgress = true;
        _state.NextPollDueAtUtc = null;
        _state.TickToken = null;
        PublishState(snapshot);
        _ = QueueDelayedMessageAsync(new RunPollCycleMessage(), PollTransitionRenderDelayMs, stoppingToken);
    }

    private async Task HandleRunPollCycleAsync(WorkflowStoreSnapshot snapshot, CancellationToken stoppingToken)
    {
        RefreshRuntimeConfig(snapshot.Workflow);
        await ReconcileRunningIssuesAsync(snapshot.Workflow, stoppingToken);

        if (snapshot.Workflow.ValidationErrors.Count > 0)
        {
            logger.LogError("action=workflow_validate outcome=failed workflow_path={WorkflowPath} reason={Reason}", snapshot.Workflow.Path, string.Join(" | ", snapshot.Workflow.ValidationErrors));
        }
        else
        {
            await DispatchEligibleIssuesAsync(snapshot.Workflow, stoppingToken);
        }

        _state.PollCheckInProgress = false;
        ScheduleTick(_state.PollIntervalMs, stoppingToken);
        PublishState(snapshot);
    }

    private async Task DispatchEligibleIssuesAsync(WorkflowDocument workflow, CancellationToken cancellationToken)
    {
        ITrackerClient tracker;
        try
        {
            tracker = trackerClientFactory.Create(workflow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "action=tracker_create outcome=failed workflow_path={WorkflowPath}", workflow.Path);
            return;
        }

        IReadOnlyList<IssueRecord> issues;
        try
        {
            issues = await tracker.FetchCandidateIssuesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "action=tracker_poll outcome=failed workflow_path={WorkflowPath}", workflow.Path);
            return;
        }

        foreach (var issue in OrchestrationPolicy.SortIssuesForDispatch(issues))
        {
            if (AvailableSlots() <= 0)
            {
                break;
            }

            if (!OrchestrationPolicy.ShouldDispatchIssue(issue, BuildSnapshot(workflow), workflow))
            {
                continue;
            }

            await DispatchIssueAsync(workflow, tracker, issue, attempt: null, cancellationToken);
        }
    }

    private async Task DispatchIssueAsync(WorkflowDocument workflow, ITrackerClient tracker, IssueRecord issue, int? attempt, CancellationToken cancellationToken)
    {
        IssueRecord refreshedIssue;
        try
        {
            refreshedIssue = (await tracker.FetchIssueStatesByIdsAsync([issue.Id], cancellationToken)).FirstOrDefault()
                ?? throw new InvalidOperationException("issue_missing_for_dispatch");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "action=dispatch outcome=revalidate_failed issue_id={IssueId} issue_identifier={IssueIdentifier}", issue.Id, issue.Identifier);
            return;
        }

        if (!OrchestrationPolicy.ShouldDispatchIssue(refreshedIssue, BuildSnapshot(workflow), workflow))
        {
            return;
        }

        var workspacePath = workspaceManager.GetWorkspacePath(workflow, refreshedIssue.Identifier);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var retryAttempt = attempt ?? 0;
        var runningEntry = new RunningIssueRuntime
        {
            IssueId = refreshedIssue.Id,
            Identifier = refreshedIssue.Identifier,
            Issue = refreshedIssue,
            WorkspacePath = workspacePath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Cancellation = linkedCts,
            RetryAttempt = retryAttempt
        };

        _state.Running[refreshedIssue.Id] = runningEntry;
        _state.Claimed.Add(refreshedIssue.Id);
        _state.RetryAttempts.Remove(refreshedIssue.Id);
        PublishState(new WorkflowStoreSnapshot(workflow, null, DateTimeOffset.UtcNow, null));

        var issueId = refreshedIssue.Id;
        _ = Task.Run(async () =>
        {
            var outcome = await agentRunner.RunAsync(
                workflow,
                refreshedIssue,
                attempt,
                update => EnqueueAsync(new WorkerUpdateMessage(issueId, update), CancellationToken.None),
                tracker.FetchIssueStatesByIdsAsync,
                linkedCts.Token);
            await EnqueueAsync(new WorkerCompletedMessage(issueId, outcome), CancellationToken.None);
        }, CancellationToken.None);
    }

    private async Task ReconcileRunningIssuesAsync(WorkflowDocument workflow, CancellationToken cancellationToken)
    {
        await ReconcileStalledRunsAsync(workflow, cancellationToken);
        if (_state.Running.Count == 0)
        {
            return;
        }

        IReadOnlyList<IssueRecord> refreshedIssues;
        try
        {
            refreshedIssues = await trackerClientFactory.Create(workflow).FetchIssueStatesByIdsAsync(_state.Running.Keys.ToArray(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "action=reconcile outcome=refresh_failed; keeping active workers");
            return;
        }

        var visibleIds = new HashSet<string>(refreshedIssues.Select(issue => issue.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var issue in refreshedIssues)
        {
            if (!_state.Running.TryGetValue(issue.Id, out var runningEntry))
            {
                continue;
            }

            if (workflow.Tracker.TerminalStates.Contains(issue.State, StringComparer.OrdinalIgnoreCase))
            {
                await TerminateRunningIssueAsync(workflow, issue.Id, cleanupWorkspace: true, cancellationToken);
            }
            else if (!issue.AssignedToWorker)
            {
                await TerminateRunningIssueAsync(workflow, issue.Id, cleanupWorkspace: false, cancellationToken);
            }
            else if (workflow.Tracker.ActiveStates.Contains(issue.State, StringComparer.OrdinalIgnoreCase))
            {
                runningEntry.Issue = issue;
            }
            else
            {
                await TerminateRunningIssueAsync(workflow, issue.Id, cleanupWorkspace: false, cancellationToken);
            }
        }

        foreach (var missingId in _state.Running.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
        {
            await TerminateRunningIssueAsync(workflow, missingId, cleanupWorkspace: false, cancellationToken);
        }
    }

    private async Task ReconcileStalledRunsAsync(WorkflowDocument workflow, CancellationToken cancellationToken)
    {
        if (workflow.Codex.StallTimeoutMs <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _state.Running.Values.ToArray())
        {
            var lastActivity = entry.LastEventAtUtc ?? entry.StartedAtUtc;
            var elapsedMs = (int)Math.Max(0, (now - lastActivity).TotalMilliseconds);
            if (elapsedMs <= workflow.Codex.StallTimeoutMs)
            {
                continue;
            }

            await TerminateRunningIssueAsync(workflow, entry.IssueId, cleanupWorkspace: false, cancellationToken);
            ScheduleRetry(workflow, entry.IssueId, NextFailureAttempt(entry), entry.Identifier, $"stalled for {elapsedMs}ms without codex activity", entry.WorkspacePath, continuation: false, cancellationToken);
        }
    }

    private async Task HandleRetryDueAsync(RetryDueMessage retryDue, WorkflowStoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_state.RetryAttempts.TryGetValue(retryDue.IssueId, out var retryEntry) || !string.Equals(retryEntry.RetryToken, retryDue.RetryToken, StringComparison.Ordinal))
        {
            return;
        }

        _state.RetryAttempts.Remove(retryDue.IssueId);
        IReadOnlyList<IssueRecord> issues;
        try
        {
            issues = await trackerClientFactory.Create(snapshot.Workflow).FetchCandidateIssuesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "action=retry outcome=poll_failed issue_id={IssueId} issue_identifier={IssueIdentifier}", retryEntry.IssueId, retryEntry.Identifier);
            ScheduleRetry(snapshot.Workflow, retryEntry.IssueId, retryEntry.Attempt + 1, retryEntry.Identifier, "retry poll failed", retryEntry.WorkspacePath, continuation: false, cancellationToken);
            PublishState(snapshot);
            return;
        }

        var issue = issues.FirstOrDefault(candidate => string.Equals(candidate.Id, retryDue.IssueId, StringComparison.OrdinalIgnoreCase));
        if (issue is null)
        {
            _state.Claimed.Remove(retryDue.IssueId);
            PublishState(snapshot);
            return;
        }

        if (AvailableSlots() <= 0)
        {
            ScheduleRetry(snapshot.Workflow, retryEntry.IssueId, retryEntry.Attempt + 1, retryEntry.Identifier, "no available orchestrator slots", retryEntry.WorkspacePath, continuation: false, cancellationToken);
            PublishState(snapshot);
            return;
        }

        await DispatchIssueAsync(snapshot.Workflow, trackerClientFactory.Create(snapshot.Workflow), issue, retryEntry.Attempt, cancellationToken);
    }

    private void HandleWorkerUpdate(WorkerUpdateMessage workerUpdate, WorkflowStoreSnapshot snapshot)
    {
        if (!_state.Running.TryGetValue(workerUpdate.IssueId, out var runningEntry))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(workerUpdate.Update.SessionId) && !string.Equals(workerUpdate.Update.SessionId, runningEntry.SessionId, StringComparison.Ordinal))
        {
            runningEntry.TurnCount += 1;
        }

        runningEntry.SessionId = workerUpdate.Update.SessionId ?? runningEntry.SessionId;
        runningEntry.CodexAppServerPid = workerUpdate.Update.CodexAppServerPid ?? runningEntry.CodexAppServerPid;
        runningEntry.LastEvent = workerUpdate.Update.Event;
        runningEntry.LastMessage = workerUpdate.Update.Message;
        runningEntry.LastEventAtUtc = workerUpdate.Update.Timestamp;
        ApplyTokenUpdate(runningEntry, workerUpdate.Update);
        if (workerUpdate.Update.RateLimits is not null)
        {
            _state.CodexRateLimits = workerUpdate.Update.RateLimits;
        }

        PublishState(snapshot);
    }

    private void HandleWorkerCompleted(WorkerCompletedMessage workerCompleted, WorkflowStoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_state.Running.TryGetValue(workerCompleted.IssueId, out var runningEntry))
        {
            return;
        }

        _state.Running.Remove(workerCompleted.IssueId);
        AddRuntimeSeconds(runningEntry);

        if (workerCompleted.Outcome.Success)
        {
            _state.Completed.Add(workerCompleted.IssueId);
            ScheduleRetry(snapshot.Workflow, workerCompleted.IssueId, 1, runningEntry.Identifier, "continuation check", runningEntry.WorkspacePath, continuation: true, cancellationToken);
        }
        else
        {
            ScheduleRetry(snapshot.Workflow, workerCompleted.IssueId, NextFailureAttempt(runningEntry), runningEntry.Identifier, workerCompleted.Outcome.Error ?? "agent exited", runningEntry.WorkspacePath, continuation: false, cancellationToken);
        }

        PublishState(snapshot);
    }

    private void HandleRefreshRequest(RefreshRequestMessage refreshRequest, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var alreadyDue = _state.NextPollDueAtUtc is not null && _state.NextPollDueAtUtc <= now;
        var coalesced = _state.PollCheckInProgress || alreadyDue;
        if (!coalesced)
        {
            ScheduleTick(0, cancellationToken);
        }

        refreshRequest.Reply.TrySetResult(new RefreshPayload(now.ToString("O"), true, coalesced, ["poll", "reconcile"]));
    }

    private async Task TerminateRunningIssueAsync(WorkflowDocument workflow, string issueId, bool cleanupWorkspace, CancellationToken cancellationToken)
    {
        if (!_state.Running.TryGetValue(issueId, out var runningEntry))
        {
            _state.Claimed.Remove(issueId);
            _state.RetryAttempts.Remove(issueId);
            return;
        }

        _state.Running.Remove(issueId);
        _state.Claimed.Remove(issueId);
        _state.RetryAttempts.Remove(issueId);
        runningEntry.Cancellation.Cancel();
        AddRuntimeSeconds(runningEntry);

        if (cleanupWorkspace)
        {
            try
            {
                await workspaceManager.RemoveIssueWorkspacesAsync(workflow, runningEntry.Identifier, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "action=workspace_cleanup outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier}", runningEntry.IssueId, runningEntry.Identifier);
            }
        }
    }

    private async Task StartupTerminalWorkspaceCleanupAsync(WorkflowDocument workflow, CancellationToken cancellationToken)
    {
        try
        {
            var terminalIssues = await trackerClientFactory.Create(workflow).FetchIssuesByStatesAsync(workflow.Tracker.TerminalStates, cancellationToken);
            foreach (var issue in terminalIssues)
            {
                await workspaceManager.RemoveIssueWorkspacesAsync(workflow, issue.Identifier, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "action=startup_cleanup outcome=failed workflow_path={WorkflowPath}", workflow.Path);
        }
    }

    private void PublishState(WorkflowStoreSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var running = _state.Running.Values
            .Select(entry => new RunningIssueState(
                entry.IssueId,
                entry.Identifier,
                entry.Issue.State,
                entry.WorkspacePath,
                entry.SessionId,
                entry.CodexAppServerPid,
                entry.TurnCount,
                entry.LastEvent,
                entry.LastMessage,
                entry.StartedAtUtc,
                entry.LastEventAtUtc,
                RuntimeSeconds(entry, now),
                new TokensPayload(entry.InputTokens, entry.OutputTokens, entry.TotalTokens)))
            .OrderBy(entry => entry.IssueIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var retrying = _state.RetryAttempts.Values
            .Select(entry => new RetryIssueState(
                entry.IssueId,
                entry.Identifier,
                entry.Attempt,
                entry.DueAtUtc,
                entry.Error,
                entry.WorkspacePath,
                DueInMs(entry.DueAtUtc, now)))
            .OrderBy(entry => entry.DueAt)
            .ToList();

        var workflowErrors = snapshot.LastReloadError is null
            ? snapshot.Workflow.ValidationErrors.ToList()
            : snapshot.Workflow.ValidationErrors.Concat([snapshot.LastReloadError]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        store.Update(new RuntimeState(
            running,
            retrying,
            _state.CodexTotals,
            _state.CodexRateLimits,
            new PollingState(_state.PollCheckInProgress, DueInMs(_state.NextPollDueAtUtc, now), _state.PollIntervalMs),
            snapshot.Workflow.Path,
            snapshot.Workflow.Workspace.Root,
            snapshot.Workflow.ValidationErrors.Count == 0,
            workflowErrors));
    }

    private void ScheduleTick(int delayMs, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        _state.TickToken = token;
        _state.NextPollDueAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(delayMs);
        _ = QueueDelayedMessageAsync(new TickMessage(token), delayMs, cancellationToken);
    }

    private void ScheduleRetry(WorkflowDocument workflow, string issueId, int attempt, string identifier, string error, string workspacePath, bool continuation, CancellationToken cancellationToken)
    {
        var delayMs = continuation && attempt == 1
            ? ContinuationRetryDelayMs
            : OrchestrationPolicy.CalculateFailureRetryDelay(attempt, workflow.Agent.MaxRetryBackoffMs);
        var retryToken = Guid.NewGuid().ToString("N");
        var dueAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(delayMs);
        _state.RetryAttempts[issueId] = new RetryIssueRuntime(issueId, identifier, attempt, dueAtUtc, retryToken, error, workspacePath);
        _state.Claimed.Add(issueId);
        _ = QueueDelayedMessageAsync(new RetryDueMessage(issueId, retryToken), delayMs, cancellationToken);
    }

    private async Task QueueDelayedMessageAsync(IRuntimeMessage message, int delayMs, CancellationToken cancellationToken)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            await EnqueueAsync(message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnqueueAsync(IRuntimeMessage message, CancellationToken cancellationToken)
    {
        if (!_messages.Writer.TryWrite(message))
        {
            await _messages.Writer.WriteAsync(message, cancellationToken);
        }
    }

    private int AvailableSlots() => Math.Max(_state.MaxConcurrentAgents - _state.Running.Count, 0);

    private OrchestrationSnapshot BuildSnapshot(WorkflowDocument workflow)
    {
        var runningCountsByState = _state.Running.Values
            .GroupBy(entry => entry.Issue.State.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new OrchestrationSnapshot(
            _state.Running.Keys.ToArray(),
            _state.Claimed.ToArray(),
            runningCountsByState,
            workflow.Agent.MaxConcurrentAgents,
            workflow.Agent.MaxConcurrentAgentsByState,
            workflow.Agent.MaxRetryBackoffMs);
    }

    private void ApplyTokenUpdate(RunningIssueRuntime runningEntry, CodexSessionUpdate update)
    {
        ApplyToken(update.ReportedInputTokens, runningEntry.LastReportedInputTokens, input: true, output: false, total: false, apply: delta =>
        {
            runningEntry.InputTokens += delta;
            runningEntry.LastReportedInputTokens = update.ReportedInputTokens!.Value;
        });
        ApplyToken(update.ReportedOutputTokens, runningEntry.LastReportedOutputTokens, input: false, output: true, total: false, apply: delta =>
        {
            runningEntry.OutputTokens += delta;
            runningEntry.LastReportedOutputTokens = update.ReportedOutputTokens!.Value;
        });
        ApplyToken(update.ReportedTotalTokens, runningEntry.LastReportedTotalTokens, input: false, output: false, total: true, apply: delta =>
        {
            runningEntry.TotalTokens += delta;
            runningEntry.LastReportedTotalTokens = update.ReportedTotalTokens!.Value;
        });
    }

    private void ApplyToken(int? reported, int lastReported, bool input, bool output, bool total, Action<int> apply)
    {
        if (reported is null || reported < lastReported)
        {
            return;
        }

        var delta = reported.Value - lastReported;
        apply(delta);
        _state.CodexTotals = new CodexTotalsPayload(
            _state.CodexTotals.InputTokens + (input ? delta : 0),
            _state.CodexTotals.OutputTokens + (output ? delta : 0),
            _state.CodexTotals.TotalTokens + (total ? delta : 0),
            _state.CodexTotals.SecondsRunning);
    }

    private void AddRuntimeSeconds(RunningIssueRuntime runningEntry)
    {
        _state.CodexTotals = _state.CodexTotals with
        {
            SecondsRunning = _state.CodexTotals.SecondsRunning + RuntimeSeconds(runningEntry, DateTimeOffset.UtcNow)
        };
    }

    private static int RuntimeSeconds(RunningIssueRuntime runningEntry, DateTimeOffset now) => Math.Max(0, (int)(now - runningEntry.StartedAtUtc).TotalSeconds);
    private static int NextFailureAttempt(RunningIssueRuntime runningEntry) => runningEntry.RetryAttempt > 0 ? runningEntry.RetryAttempt + 1 : 1;
    private static int? DueInMs(DateTimeOffset? dueAtUtc, DateTimeOffset now) => dueAtUtc is null ? null : Math.Max(0, (int)(dueAtUtc.Value - now).TotalMilliseconds);

    private interface IRuntimeMessage;
    private sealed record TickMessage(string Token) : IRuntimeMessage;
    private sealed record RunPollCycleMessage : IRuntimeMessage;
    private sealed record RetryDueMessage(string IssueId, string RetryToken) : IRuntimeMessage;
    private sealed record WorkerUpdateMessage(string IssueId, CodexSessionUpdate Update) : IRuntimeMessage;
    private sealed record WorkerCompletedMessage(string IssueId, AgentRunOutcome Outcome) : IRuntimeMessage;
    private sealed record RefreshRequestMessage(TaskCompletionSource<RefreshPayload> Reply) : IRuntimeMessage;
}


