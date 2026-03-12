namespace Symphony.DotNet.Models;

internal sealed record RuntimeState(
    IReadOnlyList<RunningIssueState> Running,
    IReadOnlyList<RetryIssueState> Retrying,
    CodexTotalsPayload CodexTotals,
    object? RateLimits,
    PollingState Polling,
    string WorkflowPath,
    string WorkspaceRoot,
    bool WorkflowValid,
    IReadOnlyList<string> WorkflowErrors);

internal sealed record PollingState(bool Checking, int? NextPollInMs, int PollIntervalMs);

internal sealed record RunningIssueState(
    string IssueId,
    string IssueIdentifier,
    string State,
    string WorkspacePath,
    string? SessionId,
    string? CodexAppServerPid,
    int TurnCount,
    string? LastEvent,
    string? LastMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastEventAt,
    int RuntimeSeconds,
    TokensPayload Tokens);

internal sealed record RetryIssueState(
    string IssueId,
    string IssueIdentifier,
    int Attempt,
    DateTimeOffset DueAt,
    string Error,
    string? WorkspacePath = null,
    int? DueInMs = null);
