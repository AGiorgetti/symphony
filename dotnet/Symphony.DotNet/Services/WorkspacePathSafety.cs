namespace Symphony.DotNet.Services;

internal static class WorkspacePathSafety
{
    public static string Canonicalize(string path)
    {
        return Path.GetFullPath(path);
    }

    public static void ValidateWorkspacePath(string workspacePath, string workspaceRoot)
    {
        var canonicalWorkspace = Canonicalize(workspacePath);
        var canonicalRoot = Canonicalize(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;

        if (string.Equals(canonicalWorkspace, canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"workspace_equals_root: {canonicalWorkspace}");
        }

        if (!canonicalWorkspace.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"workspace_outside_root: {canonicalWorkspace} :: {canonicalRoot}");
        }
    }
}
