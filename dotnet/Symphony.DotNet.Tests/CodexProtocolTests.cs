using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.DotNet.Models;
using Symphony.DotNet.Services;

internal static class CodexProtocolTests
{
    public static void CodexClientHandlesApprovalsToolsAndTelemetry()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var workspaceRoot = Path.Combine(tempRoot, "workspaces");
            var workspacePath = Path.Combine(workspaceRoot, "MT-1");
            Directory.CreateDirectory(workspacePath);

            var command = WriteFakeCodexServer(workspacePath);
            var requests = new List<RecordedGraphQlRequest>();
            var httpFactory = new StubHttpClientFactory(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync();
                requests.Add(new RecordedGraphQlRequest(
                    request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : string.Empty,
                    body));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"viewer\":{\"id\":\"viewer-1\"}}}")
                };
            });

            var workflow = new WorkflowDocument(
                Path.Combine(tempRoot, "WORKFLOW.md"),
                new Dictionary<string, object?>(),
                "Test {{ issue.identifier }}",
                new WorkflowTrackerConfig("linear", "test-token", "project", "https://api.linear.app/graphql", ["Todo"], ["Done"]),
                new WorkflowWorkspaceConfig(workspaceRoot),
                new WorkflowHookConfig(null, null, null, null, 5_000),
                new WorkflowPollingConfig(1_000),
                new WorkflowAgentConfig(1, 300_000, 1, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
                new WorkflowCodexConfig(command, "never", "workspace-write", null, 10_000, 5_000, 300_000),
                new WorkflowServerConfig(null),
                []);

            var client = new CodexAppServerClient(NullLogger<CodexAppServerClient>.Instance, httpFactory);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var session = client.StartSessionAsync(workflow, workspacePath, timeoutCts.Token).GetAwaiter().GetResult();
            try
            {
                var issue = new IssueRecord("1", "MT-1", "Test issue", "Todo", 1, null, null, "Body", null, null, [], [], true);
                var updates = new List<CodexSessionUpdate>();
                var result = session.RunTurnAsync(issue, "Do the work", update =>
                {
                    updates.Add(update);
                    return Task.CompletedTask;
                }, timeoutCts.Token).GetAwaiter().GetResult();

                Assert(result.SessionId == "thread-1-turn-1", "session id should combine thread and turn ids");
                Assert(updates.Any(update => update.Event == "approval_auto_approved"), "approval requests should be auto-approved when approval_policy is never");
                Assert(updates.Any(update => update.Event == "tool_input_auto_answered"), "user-input requests should fall back to the non-interactive answer when approval labels are unavailable");
                Assert(updates.Any(update => update.Event == "tool_call_completed"), "dynamic tool calls should be executed");
                Assert(updates.Any(update => update.Event == "turn_completed" && update.ReportedInputTokens == 11 && update.ReportedOutputTokens == 7 && update.ReportedTotalTokens == 18), "turn completion should surface token usage");
                Assert(updates.Any(update => update.RateLimits is not null), "rate limit notifications should be extracted");
                Assert(requests.Count == 1, "linear_graphql should issue one GraphQL request");
                Assert(requests[0].Authorization == "test-token", "linear_graphql should use the raw Authorization token");
                Assert(requests[0].Body.Contains("query Test", StringComparison.Ordinal), "linear_graphql should preserve the query body");
            }
            finally
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    public static void CodexLaunchUsesWindowsCommandProcessor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = CodexAppServerClient.CreateStartInfo("codex app-server", Path.GetTempPath());
        Assert(string.Equals(Path.GetFileName(startInfo.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase), "Windows launch should use the command processor instead of Git Bash");
        Assert(startInfo.ArgumentList.SequenceEqual(["/d", "/s", "/c", "codex app-server"]), "Windows launch should preserve codex.command as the final shell command argument");
    }

    private static string WriteFakeCodexServer(string workspacePath)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(workspacePath, "fake-codex.ps1");
            File.WriteAllText(scriptPath, """
$stdin = [Console]::In
while (($line = $stdin.ReadLine()) -ne $null) {
  if ($line.Contains('"method":"initialize"')) {
    Write-Output '{"id":1,"result":{"serverInfo":{"name":"fake-codex"}}}'
    continue
  }

  if ($line.Contains('"method":"thread/start"')) {
    Write-Output '{"id":2,"result":{"thread":{"id":"thread-1"}}}'
    Write-Output '{"method":"thread/started","params":{"threadId":"thread-1"}}'
    continue
  }

  if ($line.Contains('"method":"turn/start"')) {
    Write-Output '{"id":3,"result":{"turn":{"id":"turn-1"}}}'
    Write-Output '{"method":"turn/started","params":{"turnId":"turn-1"}}'
    Write-Output '{"method":"item/commandExecution/requestApproval","id":"approve-1","params":{"command":"git status"}}'
    continue
  }

  if ($line.Contains('"id":"approve-1"')) {
    Write-Output '{"method":"item/tool/requestUserInput","id":"input-1","params":{"questions":[{"id":"question-1"}]}}'
    continue
  }

  if ($line.Contains('"id":"input-1"')) {
    Write-Output '{"method":"item/tool/call","id":"tool-1","params":{"tool":"linear_graphql","arguments":{"query":"query Test { viewer { id } }","variables":{"demo":1}}}}'
    continue
  }

  if ($line.Contains('"id":"tool-1"')) {
    [Console]::Error.WriteLine('fake stderr from codex')
    Write-Output '{"method":"account/rateLimits/updated","params":{"rateLimits":{"limit_id":"demo","primary":{"remaining":12,"resetSeconds":34}}}}'
    Write-Output '{"method":"turn/completed","params":{"usage":{"input_tokens":11,"output_tokens":7,"total_tokens":18}}}'
  }
}
""");

            return "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\\fake-codex.ps1";
        }

        var bashPath = Path.Combine(workspacePath, "fake-codex.sh");
        File.WriteAllText(bashPath, NormalizeScriptLineEndings("""
#!/usr/bin/env bash
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialize"'*)
      printf '%s\n' '{"id":1,"result":{"serverInfo":{"name":"fake-codex"}}}'
      ;;
    *'"method":"thread/start"'*)
      printf '%s\n' '{"id":2,"result":{"thread":{"id":"thread-1"}}}'
      printf '%s\n' '{"method":"thread/started","params":{"threadId":"thread-1"}}'
      ;;
    *'"method":"turn/start"'*)
      printf '%s\n' '{"id":3,"result":{"turn":{"id":"turn-1"}}}'
      printf '%s\n' '{"method":"turn/started","params":{"turnId":"turn-1"}}'
      printf '%s\n' '{"method":"item/commandExecution/requestApproval","id":"approve-1","params":{"command":"git status"}}'
      ;;
    *'"id":"approve-1"'*)
      printf '%s\n' '{"method":"item/tool/requestUserInput","id":"input-1","params":{"questions":[{"id":"question-1"}]}}'
      ;;
    *'"id":"input-1"'*)
      printf '%s\n' '{"method":"item/tool/call","id":"tool-1","params":{"tool":"linear_graphql","arguments":{"query":"query Test { viewer { id } }","variables":{"demo":1}}}}'
      ;;
    *'"id":"tool-1"'*)
      printf '%s\n' 'fake stderr from codex' >&2
      printf '%s\n' '{"method":"account/rateLimits/updated","params":{"rateLimits":{"limit_id":"demo","primary":{"remaining":12,"resetSeconds":34}}}}'
      printf '%s\n' '{"method":"turn/completed","params":{"usage":{"input_tokens":11,"output_tokens":7,"total_tokens":18}}}'
      ;;
  esac
done
"""));

        return "bash ./fake-codex.sh";
    }

    private static string NormalizeScriptLineEndings(string script) => script.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "symphony-dotnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record RecordedGraphQlRequest(string Authorization, string Body);

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(sendAsync), disposeHandler: true);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => sendAsync(request);
    }
}
