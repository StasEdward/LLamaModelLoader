using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Searches public GGUF repositories and reads their file lists through the Hugging Face HTTP API. No credentials are sent.</summary>
public sealed partial class HuggingFaceClient(HttpClient http)
{
    private const string Api = "https://huggingface.co/api/models";

    [GeneratedRegex(@"^[0-9a-f]{40}\z", RegexOptions.IgnoreCase)]
    private static partial Regex CommitSha();
    [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256();
    [GeneratedRegex(@"<([^>]*)>([^,<]*)")]
    private static partial Regex LinkEntry();
    [GeneratedRegex(@";\s*rel\s*=\s*""?next""?\s*(;|\z)", RegexOptions.IgnoreCase)]
    private static partial Regex NextRel();

    public async Task<(IReadOnlyList<HfModelSummary> Models, string? NextPageUrl)> SearchAsync(string query, HfSort sort, string? nextPageUrl, CancellationToken token)
    {
        var url = nextPageUrl is not null && IsApiUrl(nextPageUrl) ? nextPageUrl
            : $"{Api}?{(query.Trim() is { Length: > 0 } text ? $"search={Uri.EscapeDataString(text)}&" : "")}filter=gguf&sort={HfNames.SortKey(sort)}&limit=30&full=false";
        var (json, link) = await GetAsync(url, token);
        return (ParseSearch(json), ParseNextLink(link));
    }

    public async Task<HfModelDetails> GetDetailsAsync(string repoId, CancellationToken token)
    {
        if (!HfNames.IsValidRepoId(repoId)) throw new ArgumentException("Invalid Hugging Face repository id.", nameof(repoId));
        var (json, _) = await GetAsync($"{Api}/{repoId}?blobs=true&expand[]=gguf&expand[]=siblings&expand[]=gated&expand[]=sha&expand[]=tags", token);
        return ParseDetails(json);
    }

    private async Task<(string Body, string? Link)> GetAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("LLamaModelLoader/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Hugging Face refused the request (rate limit). Try again later.", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, timeout.Token);
            var link = response.Headers.TryGetValues("Link", out var values) ? string.Join(", ", values) : null;
            return (await response.Content.ReadAsStringAsync(timeout.Token), link);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException("Hugging Face did not respond in time. Try again later.");
        }
    }

    public static IReadOnlyList<HfModelSummary> ParseSearch(string json)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw Unrecognized();
        var models = new List<HfModelSummary>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Str(item, "id") is not { } id || !HfNames.IsValidRepoId(id)) continue;
            models.Add(new HfModelSummary(id, Str(item, "author") ?? id[..id.IndexOf('/')], Int(item, "downloads") ?? 0, Int(item, "likes") ?? 0,
                Str(item, "lastModified") is { } modified && DateTimeOffset.TryParse(modified, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null,
                IsGated(item), Str(item, "pipeline_tag"), Tags(item)));
        }
        return models;
    }

    public static HfModelDetails ParseDetails(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        // The commit sha ends up in download URLs, so anything but 40 hex digits is not a usable response.
        if (root.ValueKind != JsonValueKind.Object || Str(root, "id") is not { } id || !HfNames.IsValidRepoId(id) ||
            Str(root, "sha") is not { } sha || !CommitSha().IsMatch(sha)) throw Unrecognized();
        var files = new List<HfFile>();
        if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
            foreach (var sibling in siblings.EnumerateArray())
            {
                if (sibling.ValueKind != JsonValueKind.Object || Str(sibling, "rfilename") is not { } path ||
                    !sibling.TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object ||
                    Str(lfs, "sha256") is not { } digest || !Sha256().IsMatch(digest)) continue;
                var size = Int(sibling, "size") ?? Int(lfs, "size") ?? 0;
                if (size > 0) files.Add(new HfFile(path, size, digest.ToLowerInvariant()));
            }
        var hasGguf = root.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object;
        var license = Tags(root).FirstOrDefault(t => t.StartsWith("license:", StringComparison.Ordinal))?["license:".Length..];
        return new HfModelDetails(id, sha.ToLowerInvariant(), IsGated(root), hasGguf ? Str(gguf, "architecture") : null,
            hasGguf ? UInt(gguf, "total") : null, hasGguf ? UInt(gguf, "context_length") : null,
            license, HfNames.GroupQuantizations(files));
    }

    public static string? ParseNextLink(string? linkHeader)
    {
        if (string.IsNullOrWhiteSpace(linkHeader)) return null;
        foreach (Match entry in LinkEntry().Matches(linkHeader))
            if (NextRel().IsMatch(entry.Groups[2].Value) && IsApiUrl(entry.Groups[1].Value)) return entry.Groups[1].Value;
        return null;
    }

    private static bool IsApiUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw Unrecognized(); }
    }

    private static InvalidDataException Unrecognized() => new("Unrecognized response from Hugging Face.");

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static ulong? UInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;

    // "gated" is false, or a string such as "auto" or "manual". Anything but an explicit false counts as gated.
    private static bool IsGated(JsonElement element) => element.TryGetProperty("gated", out var value) && value.ValueKind != JsonValueKind.False;

    private static string[] Tags(JsonElement element) =>
        element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray() : [];
}
