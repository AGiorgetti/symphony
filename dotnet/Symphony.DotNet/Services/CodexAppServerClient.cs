using System.Threading.Channels;
using System.Diagnostics;
using System.Text.Json;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class CodexAppServerClient(ILogger<CodexAppServerClient> logger, IHttpClientFactory httpClientFactory)
{
    private const int InitializeId = 1;
    private const int ThreadStartId = 2;
    private const int TurnStartId = 3;
    private const string NonInteractiveAnswer = "This is a non-interactive session. Operator input is unavailable.";

    public async Task<CodexAppSession> StartSessionAsync(WorkflowDocument workflow, string workspacePath, CancellationToken cancellationToken)
    {
        var canonicalWorkspace = PathSafety.Canonicalize(workspacePath);
        WorkspaceManager.ValidateWorkspacePath(canonicalWorkspace, workflow.Workspace.Root, workspacePath);

        var process = new Process
        {
            StartInfo = CreateStartInfo(workflow.Codex.Command, canonicalWorkspace),
            EnableRaisingEvents = true
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"codex_start_failed: {ex.Message}", ex);
        }

        var session = new CodexAppSession(workflow, canonicalWorkspace, process, logger, httpClientFactory);
        try
        {
            await session.InitializeAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveShellExecutable(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in ResolveShellArguments(command))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static string ResolveShellExecutable()
    {
        return OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "/bin/bash";
    }

    internal static IReadOnlyList<string> ResolveShellArguments(string command)
    {
        return OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", command]
            : ["-lc", command];
    }

    internal sealed class CodexAppSession : IAsyncDisposable
    {
        private readonly WorkflowDocument _workflow;
        private readonly string _workspacePath;
        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Channel<string> _stdoutLines = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;

        public CodexAppSession(WorkflowDocument workflow, string workspacePath, Process process, ILogger logger, IHttpClientFactory httpClientFactory)
        {
            _workflow = workflow;
            _workspacePath = workspacePath;
            _process = process;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _stdoutPump = Task.Run(() => PumpStdoutAsync(_lifetimeCts.Token));
            _stderrPump = Task.Run(() => PumpStderrAsync(_lifetimeCts.Token));
        }

        public string? ThreadId { get; private set; }
        public string CodexAppServerPid => _process.Id.ToString();

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await SendMessageAsync(new
            {
                method = "initialize",
                id = InitializeId,
                @params = new
                {
                    capabilities = new { experimentalApi = true },
                    clientInfo = new
                    {
                        name = "symphony-dotnet",
                        title = "Symphony .NET",
                        version = "0.1.0"
                    }
                }
            }, cancellationToken);

            await AwaitResponseAsync(InitializeId, _workflow.Codex.ReadTimeoutMs, cancellationToken);
            await SendMessageAsync(new { method = "initialized", @params = new { } }, cancellationToken);

            await SendMessageAsync(new
            {
                method = "thread/start",
                id = ThreadStartId,
                @params = new Dictionary<string, object?>
                {
                    ["approvalPolicy"] = _workflow.Codex.ApprovalPolicy ?? "never",
                    ["sandbox"] = _workflow.Codex.ThreadSandbox ?? "workspace-write",
                    ["cwd"] = _workspacePath,
                    ["dynamicTools"] = LinearDynamicToolSpecs()
                }
            }, cancellationToken);

            using var threadResult = await AwaitResponseAsync(ThreadStartId, _workflow.Codex.ReadTimeoutMs, cancellationToken);
            ThreadId = threadResult.RootElement.GetProperty("thread").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("invalid_thread_payload");
        }

        public async Task<(string SessionId, string ThreadId, string TurnId)> RunTurnAsync(
            IssueRecord issue,
            string prompt,
            Func<CodexSessionUpdate, Task> onUpdate,
            CancellationToken cancellationToken)
        {
            var threadId = ThreadId ?? throw new InvalidOperationException("codex_thread_not_started");
            await SendMessageAsync(new
            {
                method = "turn/start",
                id = TurnStartId,
                @params = new Dictionary<string, object?>
                {
                    ["threadId"] = threadId,
                    ["input"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt } },
                    ["cwd"] = _workspacePath,
                    ["title"] = $"{issue.Identifier}: {issue.Title}",
                    ["approvalPolicy"] = _workflow.Codex.ApprovalPolicy ?? "never",
                    ["sandboxPolicy"] = _workflow.Codex.TurnSandboxPolicy
                }
            }, cancellationToken);

            using var turnResult = await AwaitResponseAsync(TurnStartId, _workflow.Codex.ReadTimeoutMs, cancellationToken);
            var turnId = turnResult.RootElement.GetProperty("turn").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("invalid_turn_payload");
            var sessionId = $"{threadId}-{turnId}";
            var deadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(_workflow.Codex.TurnTimeoutMs);
            await onUpdate(BuildUpdate("session_started", sessionId, threadId, turnId, "Codex session started", null));

            while (true)
            {
                var line = await ReadStdoutLineAsync(RemainingTurnTimeout(deadlineUtc), cancellationToken);
                using var json = ParseJson(line, onUpdate);
                if (json is null)
                {
                    continue;
                }

                var root = json.RootElement;
                var method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? string.Empty : string.Empty;

                switch (method)
                {
                    case "turn/completed":
                        await onUpdate(BuildUpdate("turn_completed", sessionId, threadId, turnId, SummarizeMethod(method), root));
                        return (sessionId, threadId, turnId);
                    case "turn/failed":
                        await onUpdate(BuildUpdate("turn_failed", sessionId, threadId, turnId, SummarizeMethod(method), root));
                        throw new InvalidOperationException("codex_turn_failed");
                    case "turn/cancelled":
                        await onUpdate(BuildUpdate("turn_cancelled", sessionId, threadId, turnId, SummarizeMethod(method), root));
                        throw new InvalidOperationException("codex_turn_cancelled");
                }

                var handled = await TryHandleMessageAsync(root, sessionId, threadId, turnId, onUpdate, cancellationToken);
                if (handled == MessageHandleResult.Continue)
                {
                    continue;
                }

                throw handled == MessageHandleResult.InputRequired
                    ? new InvalidOperationException("codex_turn_input_required")
                    : new InvalidOperationException("codex_approval_required");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetimeCts.Cancel();

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await Task.WhenAll(_stdoutPump, _stderrPump);
            }
            catch
            {
            }

            _process.Dispose();
            _lifetimeCts.Dispose();
        }

        private async Task<MessageHandleResult> TryHandleMessageAsync(
            JsonElement root,
            string sessionId,
            string threadId,
            string turnId,
            Func<CodexSessionUpdate, Task> onUpdate,
            CancellationToken cancellationToken)
        {
            var method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? string.Empty : string.Empty;

            if (IsApprovalRequest(method, root))
            {
                if (AutoApproveRequests())
                {
                    await SendApprovalResponseAsync(root, method, cancellationToken);
                    await onUpdate(BuildUpdate("approval_auto_approved", sessionId, threadId, turnId, SummarizeMethod(method), root));
                    return MessageHandleResult.Continue;
                }

                await onUpdate(BuildUpdate("approval_required", sessionId, threadId, turnId, SummarizeMethod(method), root));
                return MessageHandleResult.ApprovalRequired;
            }

            if (string.Equals(method, "item/tool/call", StringComparison.Ordinal))
            {
                var result = await ExecuteToolAsync(root, cancellationToken);
                await SendMessageAsync(new
                {
                    id = ReadId(root),
                    result
                }, cancellationToken);

                await onUpdate(BuildUpdate(IsSuccessResult(result) ? "tool_call_completed" : "tool_call_failed", sessionId, threadId, turnId, SummarizeMethod(method), root));
                return MessageHandleResult.Continue;
            }

            if (string.Equals(method, "item/tool/requestUserInput", StringComparison.Ordinal))
            {
                if (await TryAnswerUserInputAsync(root, cancellationToken))
                {
                    await onUpdate(BuildUpdate("tool_input_auto_answered", sessionId, threadId, turnId, SummarizeMethod(method), root));
                    return MessageHandleResult.Continue;
                }

                await onUpdate(BuildUpdate("turn_input_required", sessionId, threadId, turnId, SummarizeMethod(method), root));
                return MessageHandleResult.InputRequired;
            }

            if (NeedsInput(method, root))
            {
                await onUpdate(BuildUpdate("turn_input_required", sessionId, threadId, turnId, SummarizeMethod(method), root));
                return MessageHandleResult.InputRequired;
            }

            await onUpdate(BuildUpdate("notification", sessionId, threadId, turnId, SummarizeMethod(method), root));
            return MessageHandleResult.Continue;
        }

        private async Task<JsonDocument> AwaitResponseAsync(int requestId, int timeoutMs, CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await ReadStdoutLineAsync(timeoutMs, cancellationToken);
                using var json = ParseJson(line, onUpdate: null);
                if (json is null)
                {
                    continue;
                }

                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number || idProp.GetInt32() != requestId)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var errorProp))
                {
                    throw new InvalidOperationException($"codex_response_error: {errorProp.GetRawText()}");
                }

                if (!root.TryGetProperty("result", out var resultProp))
                {
                    throw new InvalidOperationException("codex_response_missing_result");
                }

                return JsonDocument.Parse(resultProp.GetRawText());
            }
        }

        private static int RemainingTurnTimeout(DateTimeOffset deadlineUtc)
        {
            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            return remaining.TotalMilliseconds > int.MaxValue ? int.MaxValue : (int)remaining.TotalMilliseconds;
        }

        private async Task<string> ReadStdoutLineAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            try
            {
                return await _stdoutLines.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException("codex_response_timeout", ex);
            }
            catch (ChannelClosedException ex)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"codex_port_exit:{_process.ExitCode}", ex);
                }

                throw new InvalidOperationException("codex_stream_closed", ex);
            }
        }

        private JsonDocument? ParseJson(string line, Func<CodexSessionUpdate, Task>? onUpdate)
        {
            try
            {
                return JsonDocument.Parse(line);
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogDebug("Codex stream output: {Line}", line);
                }

                if (onUpdate is not null)
                {
                    onUpdate(BuildUpdate("malformed", null, ThreadId, null, line.Trim(), null)).GetAwaiter().GetResult();
                }

                return null;
            }
        }

        private async Task PumpStdoutAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    await _stdoutLines.Writer.WriteAsync(line, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _stdoutLines.Writer.TryComplete();
            }
        }

        private async Task PumpStderrAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger.LogWarning("Codex stderr: {Line}", line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task SendMessageAsync(object payload, CancellationToken cancellationToken)
        {
            var line = JsonSerializer.Serialize(payload) + "\n";
            await _process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }

        private async Task SendApprovalResponseAsync(JsonElement root, string method, CancellationToken cancellationToken)
        {
            var decision = method switch
            {
                "item/commandExecution/requestApproval" => "acceptForSession",
                "item/fileChange/requestApproval" => "acceptForSession",
                _ => "approved_for_session"
            };

            await SendMessageAsync(new
            {
                id = ReadId(root),
                result = new { decision }
            }, cancellationToken);
        }

        private async Task<bool> TryAnswerUserInputAsync(JsonElement root, CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty("params", out var paramsProp) || !paramsProp.TryGetProperty("questions", out var questionsProp) || questionsProp.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var answers = new Dictionary<string, object?>();
            foreach (var question in questionsProp.EnumerateArray())
            {
                var questionId = question.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(questionId))
                {
                    return false;
                }

                var answer = AutoApproveRequests()
                    ? FindApprovalLabel(question) ?? NonInteractiveAnswer
                    : NonInteractiveAnswer;
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return false;
                }

                answers[questionId] = new { answers = new[] { answer } };
            }

            await SendMessageAsync(new
            {
                id = ReadId(root),
                result = new { answers }
            }, cancellationToken);
            return answers.Count > 0;
        }

        private async Task<object> ExecuteToolAsync(JsonElement root, CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty("params", out var paramsProp))
            {
                return FailureToolResult("Unsupported dynamic tool payload.");
            }

            var toolName = paramsProp.TryGetProperty("tool", out var toolProp)
                ? toolProp.GetString()
                : paramsProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var arguments = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

            if (!string.Equals(toolName, "linear_graphql", StringComparison.Ordinal))
            {
                return FailureToolResult($"Unsupported dynamic tool: {toolName ?? "null"}.");
            }

            string? query = null;
            object? variables = new { };
            if (arguments.ValueKind == JsonValueKind.String)
            {
                query = arguments.GetString();
            }
            else if (arguments.ValueKind == JsonValueKind.Object)
            {
                if (arguments.TryGetProperty("query", out var queryProp))
                {
                    query = queryProp.GetString();
                }

                if (arguments.TryGetProperty("variables", out var variablesProp))
                {
                    if (variablesProp.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    {
                        return FailureToolResult("`linear_graphql.variables` must be an object or null.");
                    }

                    variables = variablesProp.ValueKind == JsonValueKind.Null
                        ? new { }
                        : JsonSerializer.Deserialize<object>(variablesProp.GetRawText()) ?? new { };
                }
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return FailureToolResult("`linear_graphql` requires a non-empty `query` string.");
            }

            try
            {
                using var httpClient = _httpClientFactory.CreateClient(nameof(LinearTrackerClient));
                using var response = await LinearTrackerClient.ExecuteHttpGraphQlAsync(httpClient, _workflow, query, variables ?? new { }, cancellationToken);
                var payload = JsonSerializer.Deserialize<object>(response.RootElement.GetRawText());
                var hasErrors = response.RootElement.TryGetProperty("errors", out var errorsProp) && errorsProp.ValueKind == JsonValueKind.Array && errorsProp.GetArrayLength() > 0;
                return new Dictionary<string, object?>
                {
                    ["success"] = !hasErrors,
                    ["contentItems"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "inputText",
                            ["text"] = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return FailureToolResult($"Linear GraphQL tool execution failed: {ex.Message}");
            }
        }

        private bool AutoApproveRequests() => _workflow.Codex.ApprovalPolicy switch
        {
            string value => string.Equals(value, "never", StringComparison.OrdinalIgnoreCase),
            JsonElement json when json.ValueKind == JsonValueKind.String => string.Equals(json.GetString(), "never", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        private static bool IsApprovalRequest(string method, JsonElement root) =>
            method is "item/commandExecution/requestApproval" or "execCommandApproval" or "applyPatchApproval" or "item/fileChange/requestApproval"
            && root.TryGetProperty("id", out _);

        private static bool NeedsInput(string method, JsonElement root)
        {
            if (!method.StartsWith("turn/", StringComparison.Ordinal))
            {
                return false;
            }

            if (method is "turn/input_required" or "turn/needs_input" or "turn/need_input" or "turn/request_input" or "turn/request_response" or "turn/provide_input" or "turn/approval_required")
            {
                return true;
            }

            return root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object &&
                ((paramsProp.TryGetProperty("requiresInput", out var requiresInput) && requiresInput.ValueKind == JsonValueKind.True) ||
                 (paramsProp.TryGetProperty("needsInput", out var needsInput) && needsInput.ValueKind == JsonValueKind.True) ||
                 (paramsProp.TryGetProperty("inputRequired", out var inputRequired) && inputRequired.ValueKind == JsonValueKind.True) ||
                 (paramsProp.TryGetProperty("input_required", out var inputRequiredSnake) && inputRequiredSnake.ValueKind == JsonValueKind.True));
        }

        private static string? FindApprovalLabel(JsonElement question)
        {
            if (!question.TryGetProperty("options", out var optionsProp) || optionsProp.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var labels = optionsProp.EnumerateArray()
                .Select(option => option.TryGetProperty("label", out var labelProp) ? labelProp.GetString() : null)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();

            return labels.FirstOrDefault(label => string.Equals(label, "Approve this Session", StringComparison.OrdinalIgnoreCase))
                ?? labels.FirstOrDefault(label => string.Equals(label, "Approve Once", StringComparison.OrdinalIgnoreCase))
                ?? labels.FirstOrDefault(label => label!.TrimStart().StartsWith("Approve", StringComparison.OrdinalIgnoreCase) || label.TrimStart().StartsWith("Allow", StringComparison.OrdinalIgnoreCase));
        }

        private static object FailureToolResult(string message) => new Dictionary<string, object?>
        {
            ["success"] = false,
            ["contentItems"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "inputText",
                    ["text"] = JsonSerializer.Serialize(new { error = new { message } }, new JsonSerializerOptions { WriteIndented = true })
                }
            }
        };

        private static object ReadId(JsonElement root) => JsonSerializer.Deserialize<object>(root.GetProperty("id").GetRawText()) ?? throw new InvalidOperationException("codex_message_missing_id");

        private static bool IsSuccessResult(object result) => result is IDictionary<string, object?> dictionary && dictionary.TryGetValue("success", out var success) && success is bool boolean && boolean;

        private static IReadOnlyList<object> LinearDynamicToolSpecs() =>
        [
            new Dictionary<string, object?>
            {
                ["name"] = "linear_graphql",
                ["description"] = "Execute a raw GraphQL query or mutation against Linear using Symphony's configured auth.",
                ["inputSchema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new[] { "query" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["query"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "GraphQL query or mutation document to execute against Linear."
                        },
                        ["variables"] = new Dictionary<string, object?>
                        {
                            ["type"] = new[] { "object", "null" },
                            ["description"] = "Optional GraphQL variables object.",
                            ["additionalProperties"] = true
                        }
                    }
                }
            }
        ];

        private CodexSessionUpdate BuildUpdate(string @event, string? sessionId, string? threadId, string? turnId, string? message, JsonElement? payload)
        {
            return new CodexSessionUpdate(
                Event: @event,
                Timestamp: DateTimeOffset.UtcNow,
                Message: message,
                SessionId: sessionId,
                ThreadId: threadId,
                TurnId: turnId,
                CodexAppServerPid: CodexAppServerPid,
                ReportedInputTokens: payload is null ? null : ReadUsageValue(payload.Value, UsageKind.Input),
                ReportedOutputTokens: payload is null ? null : ReadUsageValue(payload.Value, UsageKind.Output),
                ReportedTotalTokens: payload is null ? null : ReadUsageValue(payload.Value, UsageKind.Total),
                RateLimits: payload is null ? null : ReadRateLimits(payload.Value));
        }

        private static int? ReadUsageValue(JsonElement payload, UsageKind kind)
        {
            JsonElement usage;
            if (!TryReadUsage(payload, out usage))
            {
                return null;
            }

            return kind switch
            {
                UsageKind.Input => ReadInteger(usage, "input_tokens", "prompt_tokens", "inputTokens", "promptTokens", "input"),
                UsageKind.Output => ReadInteger(usage, "output_tokens", "completion_tokens", "outputTokens", "completionTokens", "output", "completion"),
                UsageKind.Total => ReadInteger(usage, "total_tokens", "totalTokens", "total"),
                _ => null
            };
        }

        private static bool TryReadUsage(JsonElement payload, out JsonElement usage)
        {
            foreach (var path in new[]
            {
                new[] { "usage" },
                new[] { "params", "usage" },
                new[] { "params", "msg", "payload", "info", "total_token_usage" },
                new[] { "params", "msg", "info", "total_token_usage" },
                new[] { "params", "tokenUsage", "total" },
                new[] { "tokenUsage", "total" }
            })
            {
                var current = payload;
                var matched = true;
                foreach (var segment in path)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched && current.ValueKind == JsonValueKind.Object)
                {
                    usage = current;
                    return true;
                }
            }

            usage = default;
            return false;
        }

        private static object? ReadRateLimits(JsonElement payload)
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("rate_limits", out var snake))
            {
                return JsonSerializer.Deserialize<object>(snake.GetRawText());
            }

            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
            {
                if (paramsProp.TryGetProperty("rate_limits", out snake) || paramsProp.TryGetProperty("rateLimits", out snake))
                {
                    return JsonSerializer.Deserialize<object>(snake.GetRawText());
                }
            }

            return null;
        }

        private static int? ReadInteger(JsonElement payload, params string[] names)
        {
            foreach (var name in names)
            {
                if (payload.TryGetProperty(name, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var number))
                    {
                        return number;
                    }

                    if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out number))
                    {
                        return number;
                    }
                }
            }

            return null;
        }

        private static string SummarizeMethod(string method) => method switch
        {
            "thread/started" => "thread started",
            "turn/started" => "turn started",
            "turn/completed" => "turn completed",
            "account/rateLimits/updated" => "rate limits updated",
            _ => method
        };

        private enum UsageKind { Input, Output, Total }
        private enum MessageHandleResult { Continue, InputRequired, ApprovalRequired }
    }
}





