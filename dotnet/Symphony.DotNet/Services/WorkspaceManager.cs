using System.Text.RegularExpressions;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class WorkspaceManager(ILogger<WorkspaceManager> logger)
{
    private static readonly Regex UnsafeIdentifierCharacters = new("[^A-Za-z0-9._-]", RegexOptions.Compiled);
    private static readonly HashSet<string> CleanOnReuse = new(StringComparer.OrdinalIgnoreCase) { ".elixir_ls", "tmp" };

    public async Task<WorkspaceCreateResult> CreateForIssueAsync(WorkflowDocument workflow, IssueContext issue, CancellationToken cancellationToken)
    {
        var safeIdentifier = SanitizeIdentifier(issue.IssueIdentifier);
        var targetPath = Path.Combine(workflow.Workspace.Root, safeIdentifier);
        var canonicalWorkspace = CanonicalizeForCreate(targetPath);
        ValidateWorkspacePath(canonicalWorkspace, workflow.Workspace.Root, targetPath);

        var createdFresh = EnsureWorkspaceDirectory(canonicalWorkspace);
        if (!createdFresh)
        {
            CleanupReuseArtifacts(canonicalWorkspace);
        }

        if (createdFresh && !string.IsNullOrWhiteSpace(workflow.Hooks.AfterCreate))
        {
            await RunHookAsync(workflow, WorkspaceHookKind.AfterCreate, workflow.Hooks.AfterCreate!, canonicalWorkspace, issue, HookFailureMode.Propagate, cancellationToken);
        }

        return new WorkspaceCreateResult(canonicalWorkspace, createdFresh);
    }

    public Task RunBeforeRunHookAsync(WorkflowDocument workflow, string workspacePath, IssueContext issue, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(workflow.Hooks.BeforeRun)
            ? Task.CompletedTask
            : RunHookAsync(workflow, WorkspaceHookKind.BeforeRun, workflow.Hooks.BeforeRun!, workspacePath, issue, HookFailureMode.Propagate, cancellationToken);

    public Task RunAfterRunHookAsync(WorkflowDocument workflow, string workspacePath, IssueContext issue, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(workflow.Hooks.AfterRun)
            ? Task.CompletedTask
            : RunHookAsync(workflow, WorkspaceHookKind.AfterRun, workflow.Hooks.AfterRun!, workspacePath, issue, HookFailureMode.Ignore, cancellationToken);

    public async Task RemoveAsync(WorkflowDocument workflow, string workspacePath, CancellationToken cancellationToken)
    {
        ValidateWorkspacePath(CanonicalizeExistingOrText(workspacePath), workflow.Workspace.Root, workspacePath);

        if (Directory.Exists(workspacePath) && !string.IsNullOrWhiteSpace(workflow.Hooks.BeforeRemove))
        {
            await RunHookAsync(workflow, WorkspaceHookKind.BeforeRemove, workflow.Hooks.BeforeRemove!, workspacePath, IssueContext.FromIdentifier(Path.GetFileName(workspacePath)), HookFailureMode.Ignore, cancellationToken);
        }

        if (Directory.Exists(workspacePath) || File.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    public async Task RemoveIssueWorkspacesAsync(WorkflowDocument workflow, string? issueIdentifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issueIdentifier))
        {
            return;
        }

        var workspacePath = Path.Combine(workflow.Workspace.Root, SanitizeIdentifier(issueIdentifier));
        try
        {
            await RemoveAsync(workflow, workspacePath, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
    }

    public string GetWorkspacePath(WorkflowDocument workflow, string issueIdentifier)
    {
        var safeIdentifier = SanitizeIdentifier(issueIdentifier);
        var targetPath = Path.Combine(workflow.Workspace.Root, safeIdentifier);
        var canonicalWorkspace = CanonicalizeForCreate(targetPath);
        ValidateWorkspacePath(canonicalWorkspace, workflow.Workspace.Root, targetPath);
        return canonicalWorkspace;
    }

    public static string SanitizeIdentifier(string? issueIdentifier) =>
        UnsafeIdentifierCharacters.Replace(string.IsNullOrWhiteSpace(issueIdentifier) ? "issue" : issueIdentifier, "_");

    public static void ValidateWorkspacePath(string canonicalWorkspace, string workspaceRoot, string expandedWorkspacePath)
    {
        string canonicalRoot;
        try
        {
            canonicalRoot = PathSafety.Canonicalize(workspaceRoot);
        }
        catch (Exception ex)
        {
            throw new WorkspacePathUnreadableException(workspaceRoot, ex.Message);
        }

        if (string.Equals(canonicalWorkspace, canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceEqualsRootException(canonicalWorkspace, canonicalRoot);
        }

        var rootPrefix = EnsureTrailingSeparator(canonicalRoot);
        var textualPrefix = EnsureTrailingSeparator(Path.GetFullPath(workspaceRoot));
        var normalizedWorkspace = EnsureTrailingSeparator(canonicalWorkspace);
        var expandedWorkspace = EnsureTrailingSeparator(Path.GetFullPath(expandedWorkspacePath));

        if (normalizedWorkspace.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (expandedWorkspace.StartsWith(textualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceSymlinkEscapeException(expandedWorkspacePath, canonicalRoot);
        }

        throw new WorkspaceOutsideRootException(canonicalWorkspace, canonicalRoot);
    }

    private async Task RunHookAsync(WorkflowDocument workflow, WorkspaceHookKind hookKind, string command, string workspacePath, IssueContext issue, HookFailureMode failureMode, CancellationToken cancellationToken)
    {
        var hookName = ToHookName(hookKind);
        logger.LogInformation("action=workspace_hook outcome=starting hook={Hook} issue_id={IssueId} issue_identifier={IssueIdentifier} workspace={Workspace}", hookName, issue.IssueId ?? "n/a", issue.IssueIdentifier, workspacePath);

        try
        {
            var (exitCode, output) = await ProcessCommandRunner.RunShellAsync(command, workspacePath, workflow.Hooks.TimeoutMs, cancellationToken);
            if (exitCode == 0)
            {
                return;
            }

            logger.LogWarning("action=workspace_hook outcome=failed hook={Hook} issue_id={IssueId} issue_identifier={IssueIdentifier} workspace={Workspace} exit_code={ExitCode}", hookName, issue.IssueId ?? "n/a", issue.IssueIdentifier, workspacePath, exitCode);
            if (failureMode == HookFailureMode.Propagate)
            {
                throw new WorkspaceHookFailedException(hookName, exitCode, output);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("action=workspace_hook outcome=timed_out hook={Hook} issue_id={IssueId} issue_identifier={IssueIdentifier} workspace={Workspace} timeout_ms={TimeoutMs}", hookName, issue.IssueId ?? "n/a", issue.IssueIdentifier, workspacePath, workflow.Hooks.TimeoutMs);
            if (failureMode == HookFailureMode.Propagate)
            {
                throw new WorkspaceHookTimeoutException(hookName, workflow.Hooks.TimeoutMs);
            }
        }
    }

    private static string CanonicalizeForCreate(string path)
    {
        try
        {
            return PathSafety.Canonicalize(path);
        }
        catch (Exception ex)
        {
            throw new WorkspacePathUnreadableException(path, ex.Message);
        }
    }

    private static string CanonicalizeExistingOrText(string path)
    {
        try
        {
            return PathSafety.Canonicalize(path);
        }
        catch (Exception ex)
        {
            throw new WorkspacePathUnreadableException(path, ex.Message);
        }
    }

    private static bool EnsureWorkspaceDirectory(string workspacePath)
    {
        if (Directory.Exists(workspacePath))
        {
            return false;
        }

        if (File.Exists(workspacePath))
        {
            File.Delete(workspacePath);
        }

        Directory.CreateDirectory(workspacePath);
        return true;
    }

    private static void CleanupReuseArtifacts(string workspacePath)
    {
        foreach (var entry in CleanOnReuse)
        {
            var candidate = Path.Combine(workspacePath, entry);
            if (Directory.Exists(candidate))
            {
                Directory.Delete(candidate, recursive: true);
            }
            else if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static string EnsureTrailingSeparator(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string ToHookName(WorkspaceHookKind hookKind) => hookKind switch
    {
        WorkspaceHookKind.AfterCreate => "after_create",
        WorkspaceHookKind.BeforeRun => "before_run",
        WorkspaceHookKind.AfterRun => "after_run",
        WorkspaceHookKind.BeforeRemove => "before_remove",
        _ => hookKind.ToString().ToLowerInvariant()
    };
}
