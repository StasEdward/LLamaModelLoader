using System.Text.RegularExpressions;

namespace LLamaModelLoader.Infrastructure;

public sealed record ModelFile(string Path, long Size, string? Error)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Summary => $"{Name} · {Size / 1073741824d:F2} GiB" + (Error is null ? "" : " · " + Error);
    public override string ToString() => Summary;
}

public static partial class ModelCatalog
{
    [GeneratedRegex(@"^(.*)-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitName();

    public static ModelFile Inspect(string path)
    {
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) throw new IOException("An absolute path is required.");
            if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a GGUF file.");
            if (System.IO.Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)) throw new IOException("This is a projector, not a text model.");
            var match = SplitName().Match(path);
            if (!match.Success) return new(path, new FileInfo(path).Length, null);
            if (int.Parse(match.Groups[2].Value) != 1) throw new IOException("Select the first split GGUF part (00001).");
            var count = int.Parse(match.Groups[3].Value);
            if (count is < 1 or > 1000) throw new IOException("Invalid GGUF part count.");
            long size = 0;
            for (var i = 1; i <= count; i++) size += new FileInfo($"{match.Groups[1].Value}-{i:D5}-of-{count:D5}.gguf").Length;
            return new(path, size, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(path, 0, ex.Message); }
    }

    public static Task<IReadOnlyList<ModelFile>> ScanAsync(string directory, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<ModelFile>>(() =>
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The models folder was not found.");
        List<ModelFile> result = [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var path in Directory.EnumerateFiles(directory, "*.gguf", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = SplitName().Match(path);
            if (match.Success && int.Parse(match.Groups[2].Value) != 1 || System.IO.Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(Inspect(path));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }, cancellationToken);
}
