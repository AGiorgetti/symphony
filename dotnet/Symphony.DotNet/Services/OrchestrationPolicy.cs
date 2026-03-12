using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed record OrchestrationSnapshot(
    IReadOnlyCollection<string> RunningIssueIds,
    IReadOnlyCollection<string> ClaimedIssueIds,
    IReadOnlyDictionary<string, int> RunningCountsByState,
    int GlobalMaxConcurrentAgents,
    IReadOnlyDictionary<string, int> MaxConcurrentAgentsByState,
    int MaxRetryBackoffMs);

internal static class OrchestrationPolicy
{
    public static IReadOnlyList<IssueRecord> SortIssuesForDispatch(IEnumerable<IssueRecord> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(issue => issue.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool ShouldDispatchIssue(IssueRecord issue, OrchestrationSnapshot snapshot, WorkflowDocument workflow)
    {
        if (string.IsNullOrWhiteSpace(issue.Id) || string.IsNullOrWhiteSpace(issue.Identifier) || string.IsNullOrWhiteSpace(issue.Title) || string.IsNullOrWhiteSpace(issue.State))
        {
            return false;
        }

        if (!workflow.Tracker.ActiveStates.Contains(issue.State, StringComparer.OrdinalIgnoreCase) || workflow.Tracker.TerminalStates.Contains(issue.State, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!issue.AssignedToWorker)
        {
            return false;
        }

        if (snapshot.RunningIssueIds.Contains(issue.Id) || snapshot.ClaimedIssueIds.Contains(issue.Id))
        {
            return false;
        }

        if (snapshot.RunningIssueIds.Count >= snapshot.GlobalMaxConcurrentAgents)
        {
            return false;
        }

        var stateKey = issue.State.ToLowerInvariant();
        var perStateLimit = snapshot.MaxConcurrentAgentsByState.TryGetValue(stateKey, out var limit)
            ? limit
            : snapshot.GlobalMaxConcurrentAgents;
        var currentStateCount = snapshot.RunningCountsByState.TryGetValue(stateKey, out var count) ? count : 0;
        if (currentStateCount >= perStateLimit)
        {
            return false;
        }

        if (string.Equals(issue.State, "Todo", StringComparison.OrdinalIgnoreCase) && issue.BlockedBy.Any(blocker => !workflow.Tracker.TerminalStates.Contains(blocker.State, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    public static int CalculateFailureRetryDelay(int attempt, int maxRetryBackoffMs)
    {
        if (attempt <= 0)
        {
            attempt = 1;
        }

        var cappedPower = Math.Min(attempt - 1, 10);
        var delay = 10_000 * (1 << cappedPower);
        return Math.Min(delay, maxRetryBackoffMs);
    }
}

