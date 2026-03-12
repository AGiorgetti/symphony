namespace Symphony.DotNet.Models;

internal sealed record WorkflowDocument(
    string Path,
    IReadOnlyDictionary<string, object?> Config,
    string PromptTemplate,
    WorkflowTrackerConfig Tracker,
    WorkflowWorkspaceConfig Workspace,
    WorkflowHookConfig Hooks,
    WorkflowPollingConfig Polling,
    WorkflowAgentConfig Agent,
    WorkflowCodexConfig Codex,
    WorkflowServerConfig Server,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsDispatchValid => ValidationErrors.Count == 0;
}

internal sealed record WorkflowTrackerConfig(
    string Kind,
    string? ApiKey,
    string? ProjectSlug,
    string Endpoint,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> TerminalStates);

internal sealed record WorkflowWorkspaceConfig(string Root);

internal sealed record WorkflowHookConfig(
    string? AfterCreate,
    string? BeforeRun,
    string? AfterRun,
    string? BeforeRemove,
    int TimeoutMs);

internal sealed record WorkflowPollingConfig(int IntervalMs);

internal sealed record WorkflowAgentConfig(
    int MaxConcurrentAgents,
    int MaxRetryBackoffMs,
    int MaxTurns,
    IReadOnlyDictionary<string, int> MaxConcurrentAgentsByState);

internal sealed record WorkflowCodexConfig(
    string Command,
    object? ApprovalPolicy,
    string? ThreadSandbox,
    object? TurnSandboxPolicy,
    int TurnTimeoutMs,
    int ReadTimeoutMs,
    int StallTimeoutMs);

internal sealed record WorkflowServerConfig(int? Port);
