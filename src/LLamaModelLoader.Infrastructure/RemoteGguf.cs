using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Reads the KV shape of a public GGUF on huggingface.co from a growing byte prefix fetched with range requests. The weights are never downloaded.</summary>
public static partial class RemoteGguf
{
    public const long PrefixLimit = 64L << 20;
    private const long FirstPrefix = 1L << 20;

    [GeneratedRegex(@"^[0-9a-f]{40}\z", RegexOptions.IgnoreCase)]
    private static partial Regex CommitSha();

    public static Uri FileUri(string repoId, string sha, string path)
    {
        if (!HfNames.IsValidRepoId(repoId)) throw new ArgumentException("Invalid Hugging Face repository id.", nameof(repoId));
        if (!CommitSha().IsMatch(sha)) throw new ArgumentException("The revision must be a 40-character commit hash.", nameof(sha));
        if (!HfNames.IsSafeRepoPath(path)) throw new ArgumentException("Unsafe repository file path.", nameof(path));
        return new($"https://huggingface.co/{repoId}/resolve/{sha}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");
    }

    public static async Task<GgufKvShape> ReadKvShapeAsync(HttpClient http, string repoId, string sha, string path, CancellationToken token)
    {
        var uri = FileUri(repoId, sha, path);
        var data = new MemoryStream();
        long target = FirstPrefix, have = 0;
        long total = long.MaxValue;
        while (true)
        {
            var end = Math.Min(target, Math.Min(PrefixLimit, total));
            total = await FetchAsync(http, uri, data, have, end, token);
            have = data.Length;
            try { return GgufReader.ReadKvShape(new MemoryStream(data.GetBuffer(), 0, (int)have, false), token); }
            catch (GgufTruncatedException)
            {
                if (have >= total) throw new InvalidDataException("The GGUF header is truncated.");
                if (have >= PrefixLimit) throw new InvalidDataException("The GGUF header exceeds the 64 MiB inspection limit.");
                target = Math.Min(target * 2, PrefixLimit);
            }
        }
    }

    // Appends bytes [have, end) to the buffer. The server may answer with fewer bytes at the end of the file; returns the total length it reports.
    private static async Task<long> FetchAsync(HttpClient http, Uri uri, MemoryStream data, long have, long end, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LLamaModelLoader/1.0");
        request.Headers.Range = new RangeHeaderValue(have, end - 1);
        request.Headers.AcceptEncoding.ParseAdd("identity"); // A compressed body would not line up with the byte offsets.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Hugging Face refused the request (rate limit). Try again later.", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException("The file was not found on Hugging Face.");
        if (response.StatusCode != HttpStatusCode.PartialContent) throw new InvalidDataException("The server does not support range requests.");
        if (response.Content.Headers.ContentRange is not { From: { } from, To: { } to, Length: { } length } || from != have || to < from || to >= end || to >= length)
            throw new InvalidDataException("The server returned an unexpected byte range.");
        var expected = to - from + 1;
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var chunk = new byte[expected];
        var read = await body.ReadAtLeastAsync(chunk, chunk.Length, false, token);
        if (read != chunk.Length || await body.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("The server returned a body that does not match the requested range.");
        data.Write(chunk);
        return length;
    }
}
