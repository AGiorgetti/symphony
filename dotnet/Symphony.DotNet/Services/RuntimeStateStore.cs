using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class RuntimeStateStore(CliOptions options)
{
    private readonly Lock _lock = new();
    private RuntimeState _state = new([], [], new CodexTotalsPayload(0, 0, 0, 0), null, options.WorkflowPath);

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
                RateLimits = _state.RateLimits
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

            return new IssuePayload
            {
                IssueIdentifier = issueIdentifier,
                IssueId = running?.IssueId ?? retry!.IssueId,
                Status = running is not null ? "running" : "retrying",
                Workspace = new WorkspacePayload(Path.Combine(Path.GetDirectoryName(_state.WorkflowPath) ?? Environment.CurrentDirectory, "workspaces", issueIdentifier)),
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

    public RefreshPayload RequestRefresh() => new(IsoNow());

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
        TurnCount = state.TurnCount,
        LastEvent = state.LastEvent,
        LastMessage = state.LastMessage,
        StartedAt = state.StartedAt.ToString("O"),
        LastEventAt = state.LastEventAt?.ToString("O"),
        Tokens = state.Tokens
    };

    private static RetryEntryPayload ToRetryPayload(RetryIssueState state) => new(
        state.IssueId,
        state.IssueIdentifier,
        state.Attempt,
        state.DueAt.ToString("O"),
        state.Error);

    private static string IsoNow() => DateTimeOffset.UtcNow.ToString("O");
}
