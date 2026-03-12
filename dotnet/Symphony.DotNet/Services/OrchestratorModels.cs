using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class OrchestratorState
{
    public int PollIntervalMs { get; set; }
    public int MaxConcurrentAgents { get; set; }
    public DateTimeOffset? NextPollDueAtUtc { get; set; }
    public bool PollCheckInProgress { get; set; }
    public string? TickToken { get; set; }
    public Dictionary<string, RunningIssueRuntime> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RetryIssueRuntime> RetryAttempts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Completed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public CodexTotalsPayload CodexTotals { get; set; } = new(0, 0, 0, 0);
    public object? CodexRateLimits { get; set; }
}

internal sealed class RunningIssueRuntime
{
    public required string IssueId { get; init; }
    public required string Identifier { get; init; }
    public required IssueRecord Issue { get; set; }
    public required string WorkspacePath { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required CancellationTokenSource Cancellation { get; init; }
    public required int RetryAttempt { get; set; }
    public string? SessionId { get; set; }
    public string? CodexAppServerPid { get; set; }
    public string? LastEvent { get; set; }
    public string? LastMessage { get; set; }
    public DateTimeOffset? LastEventAtUtc { get; set; }
    public int TurnCount { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public int LastReportedInputTokens { get; set; }
    public int LastReportedOutputTokens { get; set; }
    public int LastReportedTotalTokens { get; set; }
}

internal sealed record RetryIssueRuntime(
    string IssueId,
    string Identifier,
    int Attempt,
    DateTimeOffset DueAtUtc,
    string RetryToken,
    string Error,
    string WorkspacePath);
