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
}
