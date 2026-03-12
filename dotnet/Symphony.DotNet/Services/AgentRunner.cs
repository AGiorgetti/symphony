using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal interface IAgentRunner
{
    Task<AgentRunOutcome> RunAsync(
        WorkflowDocument workflow,
        IssueRecord issue,
        int? attempt,
        Func<CodexSessionUpdate, Task> onUpdate,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<IssueRecord>>> issueStateFetcher,
        CancellationToken cancellationToken);
}

internal sealed class CodexAgentRunner(
    WorkspaceManager workspaceManager,
    PromptRenderer promptRenderer,
    CodexAppServerClient appServerClient,
    ILogger<CodexAgentRunner> logger) : IAgentRunner
{
    public async Task<AgentRunOutcome> RunAsync(
        WorkflowDocument workflow,
        IssueRecord issue,
        int? attempt,
        Func<CodexSessionUpdate, Task> onUpdate,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<IssueRecord>>> issueStateFetcher,
        CancellationToken cancellationToken)
    {
        var issueContext = IssueContext.FromIssue(issue);
        WorkspaceCreateResult workspace;
        try
        {
            workspace = await workspaceManager.CreateForIssueAsync(workflow, issueContext, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "action=agent_run outcome=workspace_failed issue_id={IssueId} issue_identifier={IssueIdentifier}", issue.Id, issue.Identifier);
            return AgentRunOutcome.Failed($"workspace_error: {ex.Message}");
        }

        try
        {
            await workspaceManager.RunBeforeRunHookAsync(workflow, workspace.WorkspacePath, issueContext, cancellationToken);
            await using var session = await appServerClient.StartSessionAsync(workflow, workspace.WorkspacePath, cancellationToken);

            var currentIssue = issue;
            for (var turnNumber = 1; turnNumber <= workflow.Agent.MaxTurns; turnNumber++)
            {
                var prompt = turnNumber == 1
                    ? promptRenderer.Render(workflow.PromptTemplate, currentIssue, attempt)
                    : promptRenderer.RenderContinuationPrompt(turnNumber, workflow.Agent.MaxTurns);

                _ = await session.RunTurnAsync(currentIssue, prompt, onUpdate, cancellationToken);

                var refreshed = await issueStateFetcher([currentIssue.Id], cancellationToken);
                var refreshedIssue = refreshed.FirstOrDefault();
                if (refreshedIssue is null || !workflow.Tracker.ActiveStates.Contains(refreshedIssue.State, StringComparer.OrdinalIgnoreCase))
                {
                    return AgentRunOutcome.Completed();
                }

                currentIssue = refreshedIssue;
            }

            return AgentRunOutcome.Completed();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AgentRunOutcome.Failed("agent_cancelled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "action=agent_run outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier}", issue.Id, issue.Identifier);
            return AgentRunOutcome.Failed(ex.Message);
        }
        finally
        {
            try
            {
                await workspaceManager.RunAfterRunHookAsync(workflow, workspace.WorkspacePath, issueContext, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "action=agent_run outcome=after_run_ignored issue_id={IssueId} issue_identifier={IssueIdentifier}", issue.Id, issue.Identifier);
            }
        }
    }
}
