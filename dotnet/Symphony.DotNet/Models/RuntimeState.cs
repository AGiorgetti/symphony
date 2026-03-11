namespace Symphony.DotNet.Models;

internal sealed record RuntimeState(
    IReadOnlyList<RunningIssueState> Running,
    IReadOnlyList<RetryIssueState> Retrying,
    CodexTotalsPayload CodexTotals,
    object? RateLimits,
    string WorkflowPath);

internal sealed record RunningIssueState(
    string IssueId,
    string IssueIdentifier,
    string State,
    string? SessionId,
    int TurnCount,
    string? LastEvent,
    string? LastMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastEventAt,
    TokensPayload Tokens);

internal sealed record RetryIssueState(
    string IssueId,
    string IssueIdentifier,
    int Attempt,
    DateTimeOffset DueAt,
    string Error);
