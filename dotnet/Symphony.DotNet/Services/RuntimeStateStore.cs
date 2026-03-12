using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class RuntimeStateStore(CliOptions options)
{
    private readonly Lock _lock = new();
    private RuntimeState _state = new(
        [],
        [],
        new CodexTotalsPayload(0, 0, 0, 0),
        null,
        new PollingState(false, null, 30_000),
        options.WorkflowPath,
        Path.Combine(Path.GetTempPath(), "symphony_workspaces"),
        false,
        ["workflow_not_loaded"]);

    public ObservabilityStatePayload GetStatePayload()
    {
        lock (_lock)
        {
            return new ObservabilityStatePayload
            {
                GeneratedAt = IsoNow(),
                Counts = new CountsPayload(_state.Running.Count, _state.Retrying.Count),
                Running = _state.Running.Select(ToRunningPayload).ToList(),
                Retrying = _state.Retrying.Select(ToRetryPayload).ToList(),
                CodexTotals = _state.CodexTotals,
                RateLimits = _state.RateLimits,
                Polling = new PollingPayload(_state.Polling.Checking, _state.Polling.NextPollInMs, _state.Polling.PollIntervalMs)
            };
        }
    }

    public IssuePayload? GetIssuePayload(string issueIdentifier)
    {
        lock (_lock)
        {
            var running = _state.Running.FirstOrDefault(item => string.Equals(item.IssueIdentifier, issueIdentifier, StringComparison.OrdinalIgnoreCase));
            var retry = _state.Retrying.FirstOrDefault(item => string.Equals(item.IssueIdentifier, issueIdentifier, StringComparison.OrdinalIgnoreCase));
            if (running is null && retry is null)
            {
                return null;
            }

            var workspacePath = running?.WorkspacePath ?? retry?.WorkspacePath ?? Path.Combine(_state.WorkspaceRoot, issueIdentifier);
            return new IssuePayload
            {
                IssueIdentifier = issueIdentifier,
                IssueId = running?.IssueId ?? retry!.IssueId,
                Status = running is not null ? "running" : "retrying",
                Workspace = new WorkspacePayload(workspacePath),
                Attempts = new AttemptsPayload(Math.Max((retry?.Attempt ?? 0) - 1, 0), retry?.Attempt ?? 0),
                Running = running is null ? null : ToRunningPayload(running),
                Retry = retry is null ? null : ToRetryPayload(retry),
                Logs = new LogsPayload([]),
                RecentEvents = running?.LastEventAt is null || string.IsNullOrWhiteSpace(running.LastEvent)
                    ? []
                    : [new RecentEventPayload(running.LastEventAt.Value.ToString("O"), running.LastEvent!, running.LastMessage ?? running.LastEvent!)],
                LastError = retry?.Error,
                Tracked = new Dictionary<string, string>()
            };
        }
    }

    public void Update(RuntimeState state)
    {
        lock (_lock)
        {
            _state = state;
        }
    }

    private static RunningEntryPayload ToRunningPayload(RunningIssueState state) => new()
    {
        IssueId = state.IssueId,
        IssueIdentifier = state.IssueIdentifier,
        State = state.State,
        SessionId = state.SessionId,
        CodexAppServerPid = state.CodexAppServerPid,
        TurnCount = state.TurnCount,
        LastEvent = state.LastEvent,
        LastMessage = state.LastMessage,
        StartedAt = state.StartedAt.ToString("O"),
        LastEventAt = state.LastEventAt?.ToString("O"),
        RuntimeSeconds = state.RuntimeSeconds,
        Tokens = state.Tokens
    };

    private static RetryEntryPayload ToRetryPayload(RetryIssueState state) => new(
        state.IssueId,
        state.IssueIdentifier,
        state.Attempt,
        state.DueAt.ToString("O"),
        state.DueInMs,
        state.Error);

    private static string IsoNow() => DateTimeOffset.UtcNow.ToString("O");
}
