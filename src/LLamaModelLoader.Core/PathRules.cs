namespace LLamaModelLoader.Core;

public static class PathRules
{
    // Names Windows cannot create or silently rewrites (reserved devices, trailing dot/space, forbidden characters).
    public static bool IsSafeSegment(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ');
        return !(part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3]))));
    }
}
