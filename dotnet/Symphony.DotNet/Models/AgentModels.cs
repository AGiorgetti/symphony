namespace Symphony.DotNet.Models;

internal sealed record CodexSessionUpdate(
    string Event,
    DateTimeOffset Timestamp,
    string? Message,
    string? SessionId,
    string? ThreadId,
    string? TurnId,
    string? CodexAppServerPid,
    int? ReportedInputTokens,
    int? ReportedOutputTokens,
    int? ReportedTotalTokens,
    object? RateLimits);

internal sealed record AgentRunOutcome(bool Success, string? Error)
{
    public static AgentRunOutcome Completed() => new(true, null);
    public static AgentRunOutcome Failed(string error) => new(false, error);
}
