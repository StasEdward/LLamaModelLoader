using System.Text.RegularExpressions;

namespace LLamaModelLoader.Core;

public sealed record HfModelSummary(string Id, string Author, long Downloads, long Likes, DateTimeOffset? LastModified, bool Gated, string? PipelineTag, IReadOnlyList<string> Tags)
{
    public bool IsVision => PipelineTag == "image-text-to-text" || Tags.Any(t => t is "vision" or "multimodal");
}

public sealed record HfFile(string Path, long Size, string Sha256);

public sealed record HfQuantization(string Label, IReadOnlyList<HfFile> Parts)
{
    public long TotalBytes => Parts.Sum(p => p.Size);
    public string FirstPath => Parts[0].Path;
}

public sealed record DownloadProgress(long DownloadedBytes, long TotalBytes, int Part, int Parts, double BytesPerSecond);

public sealed record HfModelDetails(string Id, string Sha, bool Gated, string? Architecture, ulong? Parameters, ulong? ContextLength, string? License, IReadOnlyList<HfQuantization> Quantizations);

public enum HfSort { Downloads, Likes, LastModified, Trending }

public static partial class HfNames
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}\z")]
    private static partial Regex RepoId();

    [GeneratedRegex(@"^(.*)-(\d{5})-of-(\d{5})\.gguf\z", RegexOptions.IgnoreCase)]
    private static partial Regex ShardName();

    [GeneratedRegex(@"-\d{5}-of-\d{5}\z", RegexOptions.IgnoreCase)]
    private static partial Regex ShardSuffix();

    [GeneratedRegex(@"(?:^|[-_.])((?:UD-)?(?:I?Q\d+(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?|TQ\d_\d))\z", RegexOptions.IgnoreCase)]
    private static partial Regex QuantSuffix();

    public static string SortKey(HfSort sort) => sort switch
    {
        HfSort.Downloads => "downloads",
        HfSort.Likes => "likes",
        HfSort.LastModified => "lastModified",
        HfSort.Trending => "trendingScore",
        _ => throw new ArgumentOutOfRangeException(nameof(sort)),
    };

    public static bool IsValidRepoId(string id) => RepoId().IsMatch(id);

    public static bool IsSafeRepoPath(string path) =>
        path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(IsSafeSegment);

    // Names Windows cannot create or silently rewrites (reserved devices, trailing dot/space, forbidden characters).
    public static bool IsSafeSegment(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ');
        return !(part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3]))));
    }

    public static string QuantLabel(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var stem = ShardSuffix().Replace(name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name, "");
        var match = QuantSuffix().Match(stem);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : stem;
    }

    public static IReadOnlyList<HfQuantization> GroupQuantizations(IEnumerable<HfFile> files)
    {
        var singles = new List<HfQuantization>();
        var shards = new Dictionary<(string Prefix, int Count), List<(int Number, HfFile File)>>();
        foreach (var file in files)
        {
            if (file.Path.Contains("mmproj", StringComparison.OrdinalIgnoreCase) || !IsSafeRepoPath(file.Path)) continue;
            var m = ShardName().Match(file.Path);
            if (!m.Success) { singles.Add(new HfQuantization(QuantLabel(file.Path), [file])); continue; }
            var key = (m.Groups[1].Value, int.Parse(m.Groups[3].Value));
            if (!shards.TryGetValue(key, out var list)) shards[key] = list = [];
            list.Add((int.Parse(m.Groups[2].Value), file));
        }
        foreach (var ((_, count), list) in shards)
        {
            var parts = list.OrderBy(p => p.Number).ToList();
            if (parts.Select(p => p.Number).SequenceEqual(Enumerable.Range(1, count)))
                singles.Add(new HfQuantization(QuantLabel(parts[0].File.Path), parts.Select(p => p.File).ToList()));
        }
        return singles.OrderBy(q => q.TotalBytes).ToList();
    }
}
