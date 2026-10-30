namespace LLamaModelLoader.Infrastructure;

internal static class SafePaths
{
    public static void CheckNoReparsePoints(string path)
    {
        // Refuse junctions/symlinks anywhere in the path before writing, moving, or cleaning staging.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Managed installation paths must not contain symbolic links or junctions.");
    }

    // For user-chosen roots: the root and its ancestors are trusted, only components below it may not be links.
    // LinkTarget (not the raw ReparsePoint attribute) so cloud placeholders such as OneDrive Files On-Demand are not mistaken for links.
    public static void CheckNoReparsePointsBelow(string path, string trustedRoot)
    {
        var full = Path.GetFullPath(path); var root = Path.GetFullPath(trustedRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path is outside the trusted folder.", nameof(path));
        for (var current = full; current is not null && !current.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if ((new FileInfo(current).LinkTarget ?? new DirectoryInfo(current).LinkTarget) is not null)
                throw new DownloadRefusedException("Model download paths must not contain symbolic links or junctions.");
    }
}
