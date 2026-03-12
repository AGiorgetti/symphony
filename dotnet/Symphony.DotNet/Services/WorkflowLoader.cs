using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class WorkflowLoader
{
    public WorkflowDocument Load(string workflowPath)
    {
        if (!File.Exists(workflowPath))
        {
            throw new InvalidOperationException($"missing_workflow_file: {workflowPath}");
        }

        var content = File.ReadAllText(workflowPath);
        var (frontMatter, promptTemplate) = Split(content);
        var config = string.IsNullOrWhiteSpace(frontMatter)
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : SimpleYamlParser.ParseMap(frontMatter);

        var tracker = ReadTracker(config);
        var workspaceRoot = ResolvePath(ReadString(config, "workspace", "root")) ?? Path.Combine(Path.GetTempPath(), "symphony_workspaces");
        var hooks = new WorkflowHookConfig(
            ReadString(config, "hooks", "after_create"),
            ReadString(config, "hooks", "before_run"),
            ReadString(config, "hooks", "after_run"),
            ReadString(config, "hooks", "before_remove"),
            PositiveOrDefault(ReadInt(config, "hooks", "timeout_ms"), 60_000));
        var polling = new WorkflowPollingConfig(PositiveOrDefault(ReadInt(config, "polling", "interval_ms"), 30_000));
        var agent = new WorkflowAgentConfig(
            PositiveOrDefault(ReadInt(config, "agent", "max_concurrent_agents"), 10),
            PositiveOrDefault(ReadInt(config, "agent", "max_retry_backoff_ms"), 300_000),
            PositiveOrDefault(ReadInt(config, "agent", "max_turns"), 20),
            ReadPositiveIntMap(config, "agent", "max_concurrent_agents_by_state"));
        var codex = new WorkflowCodexConfig(
            ReadString(config, "codex", "command") ?? "codex app-server",
            ReadObject(config, "codex", "approval_policy") ?? DefaultApprovalPolicy(),
            ReadString(config, "codex", "thread_sandbox") ?? "workspace-write",
            ReadObject(config, "codex", "turn_sandbox_policy") ?? DefaultTurnSandboxPolicy(workspaceRoot),
            PositiveOrDefault(ReadInt(config, "codex", "turn_timeout_ms"), 3_600_000),
            PositiveOrDefault(ReadInt(config, "codex", "read_timeout_ms"), 5_000),
            ReadInt(config, "codex", "stall_timeout_ms") is int stall ? Math.Max(0, stall) : 300_000);
        var server = new WorkflowServerConfig(ReadInt(config, "server", "port"));

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(tracker.Kind)) errors.Add("tracker.kind is required.");
        if (!string.Equals(tracker.Kind, "linear", StringComparison.OrdinalIgnoreCase)) errors.Add($"tracker.kind '{tracker.Kind}' is unsupported.");
        if (string.IsNullOrWhiteSpace(tracker.ApiKey)) errors.Add("tracker.api_key is required after environment resolution.");
        if (string.IsNullOrWhiteSpace(tracker.ProjectSlug)) errors.Add("tracker.project_slug is required.");
        if (string.IsNullOrWhiteSpace(codex.Command)) errors.Add("codex.command is required.");

        return new WorkflowDocument(
            workflowPath,
            config,
            string.IsNullOrWhiteSpace(promptTemplate) ? "You are working on an issue from Linear." : promptTemplate.Trim(),
            tracker,
            new WorkflowWorkspaceConfig(workspaceRoot),
            hooks,
            polling,
            agent,
            codex,
            server,
            errors);
    }

    private static (string FrontMatter, string PromptTemplate) Split(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (string.Empty, normalized);
        }

        var closing = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closing < 0)
        {
            throw new InvalidOperationException("workflow_parse_error: missing closing front matter delimiter");
        }

        return (normalized[4..closing], normalized[(closing + 5)..]);
    }

    private static WorkflowTrackerConfig ReadTracker(IReadOnlyDictionary<string, object?> config)
    {
        return new WorkflowTrackerConfig(
            ReadString(config, "tracker", "kind") ?? string.Empty,
            ResolveEnv(ReadString(config, "tracker", "api_key")) ?? ResolveEnv("$LINEAR_API_KEY"),
            ReadString(config, "tracker", "project_slug"),
            ReadString(config, "tracker", "endpoint") ?? "https://api.linear.app/graphql",
            ReadList(config, "tracker", "active_states", ["Todo", "In Progress"]),
            ReadList(config, "tracker", "terminal_states", ["Closed", "Cancelled", "Canceled", "Duplicate", "Done"]));
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> root, string section, string key)
    {
        if (!root.TryGetValue(section, out var sectionValue) || sectionValue is not IReadOnlyDictionary<string, object?> sectionMap)
        {
            return null;
        }

        return sectionMap.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static object? ReadObject(IReadOnlyDictionary<string, object?> root, string section, string key)
    {
        if (!root.TryGetValue(section, out var sectionValue) || sectionValue is not IReadOnlyDictionary<string, object?> sectionMap)
        {
            return null;
        }

        return sectionMap.TryGetValue(key, out var value) ? value : null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> root, string section, string key)
    {
        var text = ReadString(root, section, key);
        return int.TryParse(text, out var parsed) ? parsed : null;
    }

    private static IReadOnlyDictionary<string, int> ReadPositiveIntMap(IReadOnlyDictionary<string, object?> root, string section, string key)
    {
        if (!root.TryGetValue(section, out var sectionValue) || sectionValue is not IReadOnlyDictionary<string, object?> sectionMap)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        if (!sectionMap.TryGetValue(key, out var value) || value is not IReadOnlyDictionary<string, object?> nested)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in nested)
        {
            if (int.TryParse(pair.Value?.ToString(), out var parsed) && parsed > 0)
            {
                result[pair.Key.Trim().ToLowerInvariant()] = parsed;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ReadList(IReadOnlyDictionary<string, object?> root, string section, string key, IReadOnlyList<string> fallback)
    {
        if (!root.TryGetValue(section, out var sectionValue) || sectionValue is not IReadOnlyDictionary<string, object?> sectionMap)
        {
            return fallback;
        }

        if (!sectionMap.TryGetValue(key, out var value) || value is not IReadOnlyList<object?> list)
        {
            return fallback;
        }

        return list.Select(item => item?.ToString() ?? string.Empty).ToList();
    }

    private static string? ResolveEnv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.StartsWith('$') ? Environment.GetEnvironmentVariable(value[1..]) : value;
    }

    private static string? ResolvePath(string? value)
    {
        var resolved = ResolveEnv(value);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        if (resolved == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (resolved.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), resolved[2..]);
        }

        if (!resolved.Contains(Path.DirectorySeparatorChar) && !resolved.Contains(Path.AltDirectorySeparatorChar) && !Path.IsPathRooted(resolved))
        {
            return resolved;
        }

        return Path.GetFullPath(resolved);
    }

    private static IReadOnlyDictionary<string, object?> DefaultApprovalPolicy()
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["reject"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sandbox_approval"] = true,
                ["rules"] = true,
                ["mcp_elicitations"] = true
            }
        };
    }

    private static IReadOnlyDictionary<string, object?> DefaultTurnSandboxPolicy(string workspaceRoot)
    {
        var writableRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.Combine(Path.GetTempPath(), "symphony_workspaces")
            : workspaceRoot);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "workspaceWrite",
            ["writableRoots"] = new List<object?> { writableRoot },
            ["readOnlyAccess"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "fullAccess"
            },
            ["networkAccess"] = false,
            ["excludeTmpdirEnvVar"] = false,
            ["excludeSlashTmp"] = false
        };
    }

    private static int PositiveOrDefault(int? value, int fallback) => value is > 0 ? value.Value : fallback;
}


