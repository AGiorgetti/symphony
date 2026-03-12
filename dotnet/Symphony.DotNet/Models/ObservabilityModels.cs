using System.Text.Json.Serialization;

namespace Symphony.DotNet.Models;

internal sealed record ErrorEnvelope(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

internal sealed record ObservabilityStatePayload
{
    [JsonPropertyName("generated_at")]
    public required string GeneratedAt { get; init; }

    [JsonPropertyName("counts")]
    public required CountsPayload Counts { get; init; }

    [JsonPropertyName("running")]
    public required IReadOnlyList<RunningEntryPayload> Running { get; init; }

    [JsonPropertyName("retrying")]
    public required IReadOnlyList<RetryEntryPayload> Retrying { get; init; }

    [JsonPropertyName("codex_totals")]
    public required CodexTotalsPayload CodexTotals { get; init; }

    [JsonPropertyName("rate_limits")]
    public object? RateLimits { get; init; }

    [JsonPropertyName("polling")]
    public PollingPayload? Polling { get; init; }
}

internal sealed record CountsPayload(
    [property: JsonPropertyName("running")] int Running,
    [property: JsonPropertyName("retrying")] int Retrying);

internal sealed record PollingPayload(
    [property: JsonPropertyName("checking")] bool Checking,
    [property: JsonPropertyName("next_poll_in_ms")] int? NextPollInMs,
    [property: JsonPropertyName("poll_interval_ms")] int PollIntervalMs);

internal sealed record RunningEntryPayload
{
    [JsonPropertyName("issue_id")]
    public required string IssueId { get; init; }

    [JsonPropertyName("issue_identifier")]
    public required string IssueIdentifier { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("codex_app_server_pid")]
    public string? CodexAppServerPid { get; init; }

    [JsonPropertyName("turn_count")]
    public int TurnCount { get; init; }

    [JsonPropertyName("last_event")]
    public string? LastEvent { get; init; }

    [JsonPropertyName("last_message")]
    public string? LastMessage { get; init; }

    [JsonPropertyName("started_at")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("last_event_at")]
    public string? LastEventAt { get; init; }

    [JsonPropertyName("runtime_seconds")]
    public int RuntimeSeconds { get; init; }

    [JsonPropertyName("tokens")]
    public required TokensPayload Tokens { get; init; }
}

internal sealed record RetryEntryPayload(
    [property: JsonPropertyName("issue_id")] string IssueId,
    [property: JsonPropertyName("issue_identifier")] string IssueIdentifier,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("due_at")] string DueAt,
    [property: JsonPropertyName("due_in_ms")] int? DueInMs,
    [property: JsonPropertyName("error")] string Error);

internal sealed record TokensPayload(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

internal sealed record CodexTotalsPayload(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("seconds_running")] int SecondsRunning);

internal sealed record IssuePayload
{
    [JsonPropertyName("issue_identifier")]
    public required string IssueIdentifier { get; init; }

    [JsonPropertyName("issue_id")]
    public required string IssueId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("workspace")]
    public required WorkspacePayload Workspace { get; init; }

    [JsonPropertyName("attempts")]
    public required AttemptsPayload Attempts { get; init; }

    [JsonPropertyName("running")]
    public RunningEntryPayload? Running { get; init; }

    [JsonPropertyName("retry")]
    public RetryEntryPayload? Retry { get; init; }

    [JsonPropertyName("logs")]
    public required LogsPayload Logs { get; init; }

    [JsonPropertyName("recent_events")]
    public required IReadOnlyList<RecentEventPayload> RecentEvents { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("tracked")]
    public required IReadOnlyDictionary<string, string> Tracked { get; init; }
}

internal sealed record WorkspacePayload([property: JsonPropertyName("path")] string Path);
internal sealed record AttemptsPayload([property: JsonPropertyName("restart_count")] int RestartCount, [property: JsonPropertyName("current_retry_attempt")] int CurrentRetryAttempt);
internal sealed record LogsPayload([property: JsonPropertyName("codex_session_logs")] IReadOnlyList<string> CodexSessionLogs);
internal sealed record RecentEventPayload([property: JsonPropertyName("at")] string At, [property: JsonPropertyName("event")] string Event, [property: JsonPropertyName("message")] string Message);
internal sealed record RefreshPayload(
    [property: JsonPropertyName("requested_at")] string RequestedAt,
    [property: JsonPropertyName("queued")] bool Queued,
    [property: JsonPropertyName("coalesced")] bool Coalesced,
    [property: JsonPropertyName("operations")] IReadOnlyList<string> Operations);
