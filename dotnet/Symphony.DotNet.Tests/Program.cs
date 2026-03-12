using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.DotNet.Models;
using Symphony.DotNet.Services;

var failures = new List<string>();
var dotnetRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var runtimeExe = Path.Combine(dotnetRoot, "Symphony.DotNet", "bin", "Debug", "net10.0", "Symphony.DotNet.exe");
var workflowPath = Path.GetFullPath(Path.Combine(dotnetRoot, "..", "elixir", "WORKFLOW.md"));
var missingWorkflowPath = Path.Combine(dotnetRoot, "missing-WORKFLOW.md");

Run("Workflow loader parses config defaults and env resolution", WorkflowLoaderParsesConfig);
Run("Workflow loader applies Codex defaults", WorkflowLoaderAppliesCodexDefaults);
Run("Workflow store keeps last known good on invalid reload", WorkflowStoreKeepsLastKnownGood);
Run("Prompt renderer renders strict conditionals and variables", PromptRendererRendersTemplate);
Run("Workflow loader preserves bare workspace roots", WorkflowLoaderPreservesBareWorkspaceRoot);
Run("Workflow loader disables stall detection when configured", WorkflowLoaderDisablesStallDetection);
Run("Codex launch uses Windows command processor", CodexProtocolTests.CodexLaunchUsesWindowsCommandProcessor);
Run("Codex client handles approvals, tools, and telemetry", CodexProtocolTests.CodexClientHandlesApprovalsToolsAndTelemetry);
Run("Workspace manager reuses existing directory and preserves local changes", WorkspaceManagerReusesDirectory);
Run("Workspace manager surfaces after_create failures", WorkspaceManagerSurfacesHookFailure);
Run("Workspace manager ignores before_remove failure", WorkspaceManagerIgnoresBeforeRemoveFailure);
Run("Workspace manager rejects workspace root removal", WorkspaceManagerRejectsRootRemoval);
Run("Orchestration policy sorts and filters candidates", OrchestrationPolicyFiltersCandidates);
Run("Linear tracker normalizes labels and blockers", LinearTrackerNormalizesIssue);
Run("Linear tracker skips empty state fetch without API call", LinearTrackerSkipsEmptyStateFetch);
Run("Linear tracker candidate fetch uses project and active states", LinearTrackerUsesProjectAndStatesForCandidateFetch);
Run("Linear tracker surfaces GraphQL errors on poll", LinearTrackerSurfacesGraphQlErrorsOnPoll);
Run("Linear GraphQL HTTP maps transport and payload failures", LinearGraphQlHttpMapsFailures);
Run("Linear tracker paginates issue-state fetches by id", LinearTrackerPaginatesIssueStateFetches);
Run("Valid workflow host serves observability endpoints", () => WithHost(runtimeExe, 5087, workflowPath, true, client =>
{
    var state = WaitForState(client, root => root.GetProperty("polling").GetProperty("poll_interval_ms").GetInt32() > 0);
    Assert(state.RootElement.GetProperty("counts").GetProperty("running").GetInt32() == 0, "runtime should not invent placeholder running entries");

    using var refreshResponse = client.PostAsync("/api/v1/refresh", content: null).GetAwaiter().GetResult();
    Assert(refreshResponse.StatusCode == HttpStatusCode.Accepted, "refresh should return 202");
    var refreshPayload = ParseJson(refreshResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    Assert(refreshPayload.RootElement.GetProperty("queued").GetBoolean(), "refresh should be queued");

    using var wrongMethod = client.PostAsync("/api/v1/state", content: null).GetAwaiter().GetResult();
    Assert(wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed, "POST /api/v1/state should return 405");

    using var missingIssue = client.GetAsync("/api/v1/DOES-NOT-EXIST").GetAwaiter().GetResult();
    Assert(missingIssue.StatusCode == HttpStatusCode.NotFound, "missing issue should return 404");

    using var missingRoute = client.GetAsync("/totally-missing-route").GetAwaiter().GetResult();
    Assert(missingRoute.StatusCode == HttpStatusCode.NotFound, "unknown route should return 404");
}));
Run("Missing workflow fails startup cleanly", () =>
{
    var process = StartHost(runtimeExe, 5088, missingWorkflowPath, false);
    try
    {
        process.WaitForExit(5000);
        Assert(process.HasExited, "missing workflow should fail startup");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert(output.Contains("missing_workflow_file", StringComparison.OrdinalIgnoreCase), "startup failure should mention missing_workflow_file");
    }
    finally
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }

        process.Dispose();
    }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine("Test failures:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    Environment.Exit(1);
}

Console.WriteLine("All Symphony.DotNet smoke tests passed.");

void WorkflowLoaderParsesConfig()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workspaceRoot = Path.Combine(tempRoot, "workspaces");
        Environment.SetEnvironmentVariable("SYMP_TEST_LINEAR_API_KEY", "env-token");
        var workflowFile = Path.Combine(tempRoot, "WORKFLOW.md");
        File.WriteAllText(workflowFile, """
---
tracker:
  kind: linear
  api_key: $SYMP_TEST_LINEAR_API_KEY
  project_slug: test-project
polling:
  interval_ms: 1500
workspace:
  root: {{workspaceRoot}}
hooks:
  after_create: |
    {{HookWriteFile("created.txt", "created")}}
agent:
  max_concurrent_agents: 4
  max_retry_backoff_ms: 12345
  max_turns: 3
  max_concurrent_agents_by_state:
    Todo: 1
codex:
  command: codex app-server
  read_timeout_ms: 42
server:
  port: 9123
---
Hello issue
""");

        var workflow = new WorkflowLoader().Load(workflowFile);
        Assert(workflow.Tracker.ApiKey == "env-token", "workflow should resolve env-backed API key");
        Assert(workflow.Polling.IntervalMs == 1500, "workflow should parse polling interval");
        Assert(workflow.Agent.MaxConcurrentAgents == 4, "workflow should parse agent concurrency");
        Assert(workflow.Agent.MaxConcurrentAgentsByState.TryGetValue("todo", out var todoLimit) && todoLimit == 1, "workflow should normalize per-state limits");
        Assert(workflow.Codex.ReadTimeoutMs == 42, "workflow should parse codex read timeout");
        Assert(workflow.Server.Port == 9123, "workflow should parse server port");
        Assert(workflow.ValidationErrors.Count == 0, "valid workflow should not surface validation errors");
    }
    finally
    {
        Environment.SetEnvironmentVariable("SYMP_TEST_LINEAR_API_KEY", null);
        Directory.Delete(tempRoot, recursive: true);
    }
}

void WorkflowLoaderPreservesBareWorkspaceRoot()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflowFile = Path.Combine(tempRoot, "WORKFLOW.md");
        File.WriteAllText(workflowFile, """
---
tracker:
  kind: linear
  api_key: token
  project_slug: bare-root-project
workspace:
  root: repo-workspaces
codex:
  command: codex app-server
---
Hello {{ issue.identifier }}
""");

        var workflow = new WorkflowLoader().Load(workflowFile);
        Assert(workflow.Workspace.Root == "repo-workspaces", "bare workspace roots should be preserved as-is");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void WorkflowLoaderAppliesCodexDefaults()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflowFile = Path.Combine(tempRoot, "WORKFLOW.md");
        File.WriteAllText(workflowFile, """
---
tracker:
  kind: linear
  api_key: token
  project_slug: default-codex-project
workspace:
  root: repo-workspaces
---
Hello {{ issue.identifier }}
""");

        var workflow = new WorkflowLoader().Load(workflowFile);
        var approvalPolicy = workflow.Codex.ApprovalPolicy as IReadOnlyDictionary<string, object?>;
        Assert(approvalPolicy is not null, "approval policy should default to the reject-map shape");
        var rejectMap = approvalPolicy!["reject"] as IReadOnlyDictionary<string, object?>;
        Assert(rejectMap is not null, "approval policy should expose a reject section");
        Assert(rejectMap!.TryGetValue("sandbox_approval", out var sandboxApproval) && sandboxApproval is true, "approval policy should reject sandbox approvals by default");
        var sandboxPolicy = workflow.Codex.TurnSandboxPolicy as IReadOnlyDictionary<string, object?>;
        Assert(sandboxPolicy is not null, "turn sandbox policy should default to a concrete policy");
        Assert(sandboxPolicy!.TryGetValue("type", out var policyType) && string.Equals(policyType?.ToString(), "workspaceWrite", StringComparison.Ordinal), "turn sandbox policy should default to workspaceWrite");
        Assert(sandboxPolicy.TryGetValue("writableRoots", out var writableRootsValue) && writableRootsValue is IReadOnlyList<object?> writableRoots && writableRoots.Count == 1 && string.Equals(writableRoots[0]?.ToString(), Path.GetFullPath("repo-workspaces"), StringComparison.OrdinalIgnoreCase), "turn sandbox policy should target the effective workspace root");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void WorkflowLoaderDisablesStallDetection()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflowFile = Path.Combine(tempRoot, "WORKFLOW.md");
        File.WriteAllText(workflowFile, """
---
tracker:
  kind: linear
  api_key: token
  project_slug: no-stall-project
codex:
  stall_timeout_ms: 0
---
Hello {{ issue.identifier }}
""");

        var workflow = new WorkflowLoader().Load(workflowFile);
        Assert(workflow.Codex.StallTimeoutMs == 0, "stall_timeout_ms <= 0 should disable stall detection");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}
void WorkflowStoreKeepsLastKnownGood()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflowFile = Path.Combine(tempRoot, "WORKFLOW.md");
        File.WriteAllText(workflowFile, """
---
tracker:
  kind: linear
  api_key: token
  project_slug: stable-project
codex:
  command: codex app-server
---
Hello {{ issue.identifier }}
""");

        var store = new WorkflowStore(new CliOptions(workflowFile, null, true), NullLogger<WorkflowStore>.Instance);
        var initial = store.LoadInitial();
        Assert(initial.Workflow.Tracker.ProjectSlug == "stable-project", "initial workflow should load");

        File.Delete(workflowFile);
        var reloaded = store.ForceReload();
        Assert(reloaded.Workflow.ValidationErrors.Count == 0, "invalid reload should keep the last known good workflow active");
        Assert(!string.IsNullOrWhiteSpace(reloaded.LastReloadError), "invalid reload should surface an error");
        store.Dispose();
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void PromptRendererRendersTemplate()
{
    var renderer = new PromptRenderer();
    var issue = new IssueRecord("1", "MT-1", "Test", "Todo", 1, null, null, "Body", null, "https://example.org", ["backend", "api"], [], true);
    var rendered = renderer.Render("""
{{ issue.identifier }}
{% if attempt %}
retry {{ attempt }}
{% else %}
first pass
{% endif %}
labels: {{ issue.labels }}
""", issue, 2);

    Assert(rendered.Contains("MT-1", StringComparison.Ordinal), "renderer should substitute issue variables");
    Assert(rendered.Contains("retry 2", StringComparison.Ordinal), "renderer should evaluate truthy conditionals");
    Assert(rendered.Contains("backend, api", StringComparison.Ordinal), "renderer should join string lists");
}

void WorkspaceManagerReusesDirectory()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflow = BuildWorkflow(tempRoot, afterCreate: HookWriteFile("README.txt", "first"));
        var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var issue = IssueContext.FromIdentifier("MT/Det");

        var first = manager.CreateForIssueAsync(workflow, issue, CancellationToken.None).GetAwaiter().GetResult();
        Assert(first.CreatedFresh, "first workspace creation should be fresh");
        Assert(Path.GetFileName(first.WorkspacePath) == "MT_Det", "identifier should be sanitized deterministically");

        File.WriteAllText(Path.Combine(first.WorkspacePath, "README.txt"), "changed\n");
        File.WriteAllText(Path.Combine(first.WorkspacePath, "local-progress.txt"), "in progress\n");
        Directory.CreateDirectory(Path.Combine(first.WorkspacePath, "tmp"));
        File.WriteAllText(Path.Combine(first.WorkspacePath, "tmp", "scratch.txt"), "remove me");

        var second = manager.CreateForIssueAsync(workflow, issue, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!second.CreatedFresh, "second workspace creation should reuse existing directory");
        Assert(first.WorkspacePath == second.WorkspacePath, "workspace path should be deterministic");
        Assert(File.ReadAllText(Path.Combine(second.WorkspacePath, "README.txt")) == "changed\n", "after_create should not rerun on reuse");
        Assert(File.Exists(Path.Combine(second.WorkspacePath, "local-progress.txt")), "local changes should be preserved on reuse");
        Assert(!File.Exists(Path.Combine(second.WorkspacePath, "tmp", "scratch.txt")), "tmp artifacts should be removed on reuse");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void WorkspaceManagerSurfacesHookFailure()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflow = BuildWorkflow(tempRoot, afterCreate: HookFail("nope"));
        var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);

        try
        {
            _ = manager.CreateForIssueAsync(workflow, IssueContext.FromIdentifier("MT-FAIL"), CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected workspace hook failure.");
        }
        catch (WorkspaceHookFailedException)
        {
        }
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void WorkspaceManagerIgnoresBeforeRemoveFailure()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflow = BuildWorkflow(tempRoot, beforeRemove: HookFail("remove-fail"));
        var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var workspace = manager.CreateForIssueAsync(workflow, IssueContext.FromIdentifier("MT-RM"), CancellationToken.None).GetAwaiter().GetResult();

        manager.RemoveIssueWorkspacesAsync(workflow, "MT-RM", CancellationToken.None).GetAwaiter().GetResult();
        Assert(!Directory.Exists(workspace.WorkspacePath), "before_remove failure should not block deletion");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void WorkspaceManagerRejectsRootRemoval()
{
    var tempRoot = CreateTempDir();
    try
    {
        var workflow = BuildWorkflow(tempRoot);
        var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);

        try
        {
            manager.RemoveAsync(workflow, workflow.Workspace.Root, CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected workspace root rejection.");
        }
        catch (WorkspaceEqualsRootException)
        {
        }
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

void OrchestrationPolicyFiltersCandidates()
{
    var workflow = BuildWorkflow(CreateTempDir());
    var oldHigh = new IssueRecord("1", "MT-200", "Old high", "Todo", 1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null, null, [], [], true);
    var newHigh = new IssueRecord("2", "MT-201", "New high", "Todo", 1, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), null, null, null, null, [], [], true);
    var blocked = new IssueRecord("3", "MT-999", "Blocked", "Todo", 0, new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero), null, null, null, null, [], [new IssueBlocker("b1", "MT-B", "In Progress")], true);

    var sorted = OrchestrationPolicy.SortIssuesForDispatch([newHigh, oldHigh, blocked]);
    Assert(sorted.Select(item => item.Identifier).SequenceEqual(["MT-999", "MT-200", "MT-201"]), "issues should sort by priority, age, then identifier");

    var snapshot = new OrchestrationSnapshot(
        RunningIssueIds: [],
        ClaimedIssueIds: [],
        RunningCountsByState: new Dictionary<string, int>(),
        GlobalMaxConcurrentAgents: 3,
        MaxConcurrentAgentsByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        MaxRetryBackoffMs: 300_000);

    Assert(!OrchestrationPolicy.ShouldDispatchIssue(blocked, snapshot, workflow), "todo issue with non-terminal blocker should be ineligible");
    Assert(OrchestrationPolicy.ShouldDispatchIssue(oldHigh, snapshot, workflow), "eligible issue should dispatch");
    Assert(OrchestrationPolicy.CalculateFailureRetryDelay(3, 300_000) == 40_000, "failure retry backoff should be exponential");
    Assert(OrchestrationPolicy.CalculateFailureRetryDelay(12, int.MaxValue) == 10_000 * (1 << 10), "failure retry backoff should cap exponent at 10");
    Directory.Delete(workflow.Workspace.Root, recursive: true);
}

void LinearTrackerNormalizesIssue()
{
    using var json = ParseJson("""
{
  "id": "issue-1",
  "identifier": "MT-1",
  "title": "Blocked todo",
  "description": "Needs dependency",
  "priority": 2,
  "state": { "name": "Todo" },
  "branchName": "mt-1",
  "url": "https://example.org/issues/MT-1",
  "labels": { "nodes": [{ "name": "Backend" }] },
  "inverseRelations": {
    "nodes": [
      {
        "type": "blocks",
        "issue": {
          "id": "issue-2",
          "identifier": "MT-2",
          "state": { "name": "In Progress" }
        }
      },
      {
        "type": "relatesTo",
        "issue": {
          "id": "issue-3",
          "identifier": "MT-3",
          "state": { "name": "Done" }
        }
      }
    ]
  },
  "createdAt": "2026-01-01T00:00:00Z",
  "updatedAt": "2026-01-02T00:00:00Z"
}
""");

    var issue = LinearTrackerClient.NormalizeIssue(json.RootElement);
    Assert(issue is not null, "normalized issue should not be null");
    Assert(issue!.Labels.SequenceEqual(["backend"]), "labels should be lowercased");
    Assert(issue.BlockedBy.Count == 1 && issue.BlockedBy[0].Identifier == "MT-2", "only blocking inverse relations should be preserved");
    Assert(issue.Priority == 2 && issue.State == "Todo", "priority and state should be preserved");
}

void LinearTrackerSkipsEmptyStateFetch()
{
    var workflow = BuildWorkflow(CreateTempDir());
    var callCount = 0;
    var client = new LinearTrackerClient(workflow, (query, variables, cancellationToken) =>
    {
        callCount += 1;
        return Task.FromResult(JsonDocument.Parse("{}"));
    });

    var issues = client.FetchIssuesByStatesAsync([], CancellationToken.None).GetAwaiter().GetResult();
    Assert(issues.Count == 0, "empty state fetch should return no issues");
    Assert(callCount == 0, "empty state fetch should not call GraphQL");
    Directory.Delete(workflow.Workspace.Root, recursive: true);
}

void LinearTrackerUsesProjectAndStatesForCandidateFetch()
{
    var workflow = BuildWorkflow(CreateTempDir());
    object? capturedVariables = null;
    var client = new LinearTrackerClient(workflow, (query, variables, cancellationToken) =>
    {
        capturedVariables = variables;
        return Task.FromResult(JsonDocument.Parse("""
{"data":{"issues":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}
"""));
    });

    var issues = client.FetchCandidateIssuesAsync(CancellationToken.None).GetAwaiter().GetResult();
    Assert(issues.Count == 0, "empty candidate payload should return no issues");
    Assert(capturedVariables is not null, "candidate fetch should pass GraphQL variables");
    var projectSlug = capturedVariables!.GetType().GetProperty("projectSlug")!.GetValue(capturedVariables)?.ToString();
    var stateNames = (IReadOnlyList<string>)capturedVariables.GetType().GetProperty("stateNames")!.GetValue(capturedVariables)!;
    Assert(projectSlug == workflow.Tracker.ProjectSlug, "candidate fetch should pass projectSlug");
    Assert(stateNames.SequenceEqual(workflow.Tracker.ActiveStates), "candidate fetch should pass active states");
    Directory.Delete(workflow.Workspace.Root, recursive: true);
}

void LinearTrackerSurfacesGraphQlErrorsOnPoll()
{
    var workflow = BuildWorkflow(CreateTempDir());
    var client = new LinearTrackerClient(workflow, (query, variables, cancellationToken) =>
        Task.FromResult(JsonDocument.Parse("""
{"errors":[{"message":"boom"}]}
""")));

    try
    {
        _ = client.FetchCandidateIssuesAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected GraphQL errors to surface.");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message == "linear_graphql_errors", "GraphQL errors should map to linear_graphql_errors");
    }
    finally
    {
        Directory.Delete(workflow.Workspace.Root, recursive: true);
    }
}

void LinearGraphQlHttpMapsFailures()
{
    var workflow = BuildWorkflow(CreateTempDir());
    using var statusClient = new HttpClient(new StubHttpMessageHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("down")
        })));

    try
    {
        _ = LinearTrackerClient.ExecuteHttpGraphQlAsync(statusClient, workflow, "query { viewer { id } }", new { }, CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected non-success status.");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message == "linear_api_status:503", "non-success status should map to linear_api_status");
    }

    using var transportClient = new HttpClient(new StubHttpMessageHandler(_ => throw new HttpRequestException("offline")));
    try
    {
        _ = LinearTrackerClient.ExecuteHttpGraphQlAsync(transportClient, workflow, "query { viewer { id } }", new { }, CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected transport failure.");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message.Contains("linear_api_request:offline", StringComparison.Ordinal), "transport failures should map to linear_api_request");
    }

    using var payloadClient = new HttpClient(new StubHttpMessageHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json")
        })));
    try
    {
        _ = LinearTrackerClient.ExecuteHttpGraphQlAsync(payloadClient, workflow, "query { viewer { id } }", new { }, CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected payload parse failure.");
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message.StartsWith("linear_api_payload:", StringComparison.Ordinal), "malformed payloads should map to linear_api_payload");
    }
    finally
    {
        Directory.Delete(workflow.Workspace.Root, recursive: true);
    }
}

void LinearTrackerPaginatesIssueStateFetches()
{
    var workflow = BuildWorkflow(CreateTempDir());
    var issueIds = Enumerable.Range(1, 55).Select(index => $"issue-{index}").ToArray();
    var requests = new List<object>();

    var client = new LinearTrackerClient(workflow, (query, variables, cancellationToken) =>
    {
        requests.Add(variables);
        var ids = (string[])variables.GetType().GetProperty("ids")!.GetValue(variables)!;
        var nodes = ids.Select(id => new
        {
            id,
            identifier = $"MT-{id.Split('-')[1]}",
            title = $"Issue {id}",
            description = $"Description {id}",
            priority = 1,
            state = new { name = "In Progress" },
            branchName = (string?)null,
            url = (string?)null,
            assignee = new { id = "user-1" },
            labels = new { nodes = Array.Empty<object>() },
            inverseRelations = new { nodes = Array.Empty<object>() },
            createdAt = "2026-01-01T00:00:00Z",
            updatedAt = "2026-01-01T00:00:00Z"
        });

        var payload = JsonSerializer.Serialize(new { data = new { issues = new { nodes } } });
        return Task.FromResult(JsonDocument.Parse(payload));
    });

    var issues = client.FetchIssueStatesByIdsAsync(issueIds, CancellationToken.None).GetAwaiter().GetResult();
    Assert(issues.Count == 55, "issue state fetch should merge all pages");
    Assert(requests.Count == 2, "issue state fetch should paginate by 50");
}

WorkflowDocument BuildWorkflow(string tempRoot, string? afterCreate = null, string? beforeRun = null, string? afterRun = null, string? beforeRemove = null)
{
    var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["tracker"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = "linear",
            ["api_key"] = "token",
            ["project_slug"] = "project",
            ["endpoint"] = "https://api.linear.app/graphql",
            ["active_states"] = new List<object?> { "Todo", "In Progress" },
            ["terminal_states"] = new List<object?> { "Done", "Closed", "Cancelled", "Canceled", "Duplicate" }
        }
    };

    var workspaceRoot = Path.Combine(tempRoot, "workspaces");
    Directory.CreateDirectory(workspaceRoot);

    return new WorkflowDocument(
        Path.Combine(tempRoot, "WORKFLOW.md"),
        config,
        "You are working on {{ issue.identifier }}",
        new WorkflowTrackerConfig("linear", "token", "project", "https://api.linear.app/graphql", ["Todo", "In Progress"], ["Done", "Closed", "Cancelled", "Canceled", "Duplicate"]),
        new WorkflowWorkspaceConfig(workspaceRoot),
        new WorkflowHookConfig(afterCreate, beforeRun, afterRun, beforeRemove, 5_000),
        new WorkflowPollingConfig(1_000),
        new WorkflowAgentConfig(3, 300_000, 20, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
        new WorkflowCodexConfig("codex app-server", "never", "workspace-write", null, 3_600_000, 5_000, 300_000),
        new WorkflowServerConfig(null),
        []);
}

void WithHost(string exePath, int port, string workflow, bool includeLinearToken, Action<HttpClient> assertion)
{
    var process = StartHost(exePath, port, workflow, includeLinearToken);
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        WaitForHealthyState(client);
        assertion(client);
    }
    finally
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }

        process.Dispose();
    }
}

Process StartHost(string exePath, int port, string workflow, bool includeLinearToken)
{
    var startInfo = new ProcessStartInfo(exePath, $"--i-understand-that-this-will-be-running-without-the-usual-guardrails --port {port} \"{workflow}\"")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    if (includeLinearToken)
    {
        startInfo.Environment["LINEAR_API_KEY"] = "dummy-token";
    }

    return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Symphony.DotNet host.");
}

void WaitForHealthyState(HttpClient client)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);

    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var response = client.GetAsync("/api/v1/state").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch
        {
        }

        Thread.Sleep(250);
    }

    throw new InvalidOperationException("Host did not become healthy in time.");
}

JsonDocument WaitForState(HttpClient client, Func<JsonElement, bool> predicate)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        var json = ParseJson(client.GetStringAsync("/api/v1/state").GetAwaiter().GetResult());
        if (predicate(json.RootElement))
        {
            return json;
        }

        Thread.Sleep(250);
    }

    throw new InvalidOperationException("Expected runtime state did not materialize in time.");
}

string CreateTempDir()
{
    var path = Path.Combine(Path.GetTempPath(), "symphony-dotnet-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

string HookWriteFile(string fileName, string content) => OperatingSystem.IsWindows()
    ? $"echo {content} > {fileName}"
    : $"printf '{content}' > {fileName}";

string HookFail(string message) => OperatingSystem.IsWindows()
    ? $"echo {message} && exit /b 17"
    : $"echo {message}; exit 17";

JsonDocument ParseJson(string json) => JsonDocument.Parse(json);

void Run(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
    }
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => sendAsync(request);
}






