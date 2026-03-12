namespace Symphony.DotNet.Services;

internal static class PathSafety
{
    public static string Canonicalize(string path)
    {
        var expanded = Path.GetFullPath(path);
        var root = Path.GetPathRoot(expanded) ?? throw new InvalidOperationException($"path_canonicalize_failed: {expanded}");
        var remainder = expanded[root.Length..];
        var segments = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var info = new FileInfo(current);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        current = info.ResolveLinkTarget(true)?.FullName
                            ?? throw new InvalidOperationException($"path_canonicalize_failed: {expanded}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException($"path_canonicalize_failed: {expanded}: {ex.Message}", ex);
            }
        }

        return Path.GetFullPath(current);
    }
}
