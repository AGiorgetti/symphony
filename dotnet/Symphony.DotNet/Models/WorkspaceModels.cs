namespace Symphony.DotNet.Models;

internal sealed record WorkspaceCreateResult(string WorkspacePath, bool CreatedFresh);

internal class WorkspaceException(string message) : InvalidOperationException(message);
internal sealed class WorkspaceEqualsRootException(string workspacePath, string rootPath) : WorkspaceException($"workspace_equals_root: workspace={workspacePath} root={rootPath}");
internal sealed class WorkspaceOutsideRootException(string workspacePath, string rootPath) : WorkspaceException($"workspace_outside_root: workspace={workspacePath} root={rootPath}");
internal sealed class WorkspaceSymlinkEscapeException(string workspacePath, string rootPath) : WorkspaceException($"workspace_symlink_escape: workspace={workspacePath} root={rootPath}");
internal sealed class WorkspacePathUnreadableException(string path, string reason) : WorkspaceException($"workspace_path_unreadable: path={path} reason={reason}");
internal sealed class WorkspaceHookFailedException(string hookName, int exitCode, string output) : WorkspaceException($"workspace_hook_failed: hook={hookName} exit_code={exitCode} output={output}");
internal sealed class WorkspaceHookTimeoutException(string hookName, int timeoutMs) : WorkspaceException($"workspace_hook_timeout: hook={hookName} timeout_ms={timeoutMs}");
