using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Only official Windows x64 CPU, Vulkan, and CUDA archives are offered.</summary>
public sealed partial class ServerReleaseClient(HttpClient http)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")]
    private static partial Regex SafeTag();
    [GeneratedRegex(@"^(cpu|vulkan|cuda-[0-9]+\.[0-9]+)$")]
    private static partial Regex SafeBackend();
    [GeneratedRegex(@"^sha256:[a-fA-F0-9]{64}$")]
    private static partial Regex Sha256();

    public async Task<IReadOnlyList<ServerBuild>> GetBuildsAsync(CancellationToken token = default)
    {
        // /latest can be a source-only release. Binary rolling builds are often prereleases.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("LLamaModelLoader/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub refused the release check (possibly its public API rate limit). Try again later.");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, token);
        return Parse(await response.Content.ReadAsStringAsync(token));
    }

    public static IReadOnlyList<ServerBuild> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builds = new List<ServerBuild>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!SafeTag().IsMatch(tag)) continue;
            var published = release.GetProperty("published_at").GetDateTimeOffset();
            var prerelease = release.GetProperty("prerelease").GetBoolean();
            var assets = release.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(
                a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "",
                a.GetProperty("size").GetInt64(), a.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "")).ToArray();
            var prefix = $"llama-{tag}-bin-win-";
            const string suffix = "-x64.zip";
            foreach (var asset in assets.Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal) && a.Name.EndsWith(suffix, StringComparison.Ordinal)))
            {
                var backend = asset.Name[prefix.Length..^suffix.Length];
                if (!SafeBackend().IsMatch(backend)) continue;
                var parts = new List<ReleaseAsset> { asset };
                if (backend.StartsWith("cuda-", StringComparison.Ordinal))
                {
                    var runtime = assets.SingleOrDefault(a => a.Name == $"cudart-llama-bin-win-{backend}-x64.zip");
                    if (runtime is null) continue;
                    parts.Add(runtime);
                }
                var build = new ServerBuild(tag, published, prerelease, backend, parts.ToArray());
                try { Validate(build); builds.Add(build); }
                catch (InvalidDataException) { /* Unsupported or unverifiable assets are not installable. */ }
            }
        }
        return builds.OrderByDescending(b => b.PublishedAt).ThenBy(b => b.Backend, StringComparer.Ordinal).ToArray();
    }

    public static void Validate(ServerBuild build)
    {
        if (string.IsNullOrEmpty(build.Tag) || string.IsNullOrEmpty(build.Backend) || build.Assets is null ||
            !SafeTag().IsMatch(build.Tag) || !SafeBackend().IsMatch(build.Backend)) throw new InvalidDataException("Invalid build identity.");
        var names = new List<string> { $"llama-{build.Tag}-bin-win-{build.Backend}-x64.zip" };
        if (build.Backend.StartsWith("cuda-", StringComparison.Ordinal)) names.Add($"cudart-llama-bin-win-{build.Backend}-x64.zip");
        if (build.Assets.Length != names.Count) throw new InvalidDataException("The build is missing its matching runtime archive.");
        for (var i = 0; i < names.Count; i++)
        {
            var a = build.Assets[i];
            if (a is null || a.Name != names[i] || a.DownloadUrl != $"https://github.com/ggml-org/llama.cpp/releases/download/{build.Tag}/{a.Name}" ||
                a.Size is <= 0 or > 2L * 1024 * 1024 * 1024 || string.IsNullOrEmpty(a.Digest) || !Sha256().IsMatch(a.Digest))
                throw new InvalidDataException("The build must contain official archives with known sizes and SHA-256 digests.");
        }
    }
}
