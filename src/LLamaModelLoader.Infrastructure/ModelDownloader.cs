using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public enum LocalModelState { NotDownloaded, Partial, Downloaded, Conflict }

/// <summary>Downloads the parts of a public GGUF quantization into {models}\{owner}\{repo}\, resuming .part files and verifying size and SHA-256 before a part gets its final name.</summary>
public sealed class ModelDownloader
{
    private const string PartSuffix = ".part";
    private const long SpaceMargin = 256L << 20;
    private const string Corrupted = "The downloaded file is corrupted (size or SHA-256 mismatch). Download it again.";
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly Func<string, long> _freeSpace;

    public ModelDownloader(string modelsDirectory, HttpClient http, Func<string, long>? freeSpace = null)
    {
        if (!Path.IsPathFullyQualified(modelsDirectory)) throw new ArgumentException("The models folder must be a full path.", nameof(modelsDirectory));
        _root = Path.GetFullPath(modelsDirectory); _http = http;
        _freeSpace = freeSpace ?? AvailableFreeSpace;
    }

    public string Destination(string repoId, HfFile file)
    {
        // The regex alone accepts names Windows cannot create (con/x, owner/repo.), so each part also gets the segment check.
        if (!HfNames.IsValidRepoId(repoId) || !repoId.Split('/').All(HfNames.IsSafeSegment)) throw new ArgumentException("Invalid Hugging Face repository id.", nameof(repoId));
        if (!HfNames.IsSafeRepoPath(file.Path)) throw new ArgumentException("Unsafe repository file path.", nameof(file));
        var path = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(repoId.Split('/')).Concat(file.Path.Split('/')).ToArray()));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The file path is outside the models folder.", nameof(file));
        return path;
    }

    public (LocalModelState State, long PartialBytes) GetState(string repoId, HfQuantization quant)
    {
        long bytes = 0; var final = 0;
        foreach (var file in quant.Parts)
        {
            var (status, partBytes) = Inspect(Destination(repoId, file), file);
            if (status == PartStatus.Conflict) return (LocalModelState.Conflict, 0);
            if (status == PartStatus.Final) { final++; bytes += file.Size; } else bytes += partBytes;
        }
        if (quant.Parts.Count > 0 && final == quant.Parts.Count) return (LocalModelState.Downloaded, bytes);
        return (bytes == 0 ? LocalModelState.NotDownloaded : LocalModelState.Partial, bytes);
    }

    /// <summary>Downloads and verifies every part that is not final yet; returns the path of the first part.</summary>
    public async Task<string> DownloadAsync(string repoId, string sha, HfQuantization quant, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (quant.Parts.Count == 0) throw new ArgumentException("The quantization has no files.", nameof(quant));
        var parts = new List<(HfFile File, Uri Uri, string Path, bool Final)>();
        long remaining = 0;
        foreach (var file in quant.Parts)
        {
            if (file.Size <= 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException("The file size or SHA-256 is invalid.", nameof(quant));
            var uri = RemoteGguf.FileUri(repoId, sha, file.Path); var path = Destination(repoId, file);
            CheckNoLinks(path); CheckNoLinks(path + PartSuffix);
            var (status, partBytes) = Inspect(path, file);
            if (status == PartStatus.Conflict) throw new IOException($"A different file already exists at {path}.");
            if (status == PartStatus.Missing) remaining += file.Size - partBytes;
            parts.Add((file, uri, path, status == PartStatus.Final));
        }
        if (parts.All(p => p.Final)) return parts[0].Path;
        var required = remaining + SpaceMargin; var available = _freeSpace(_root);
        if (available < required) throw new IOException($"Not enough disk space: {required / 1073741824d:N2} GiB required, {available / 1073741824d:N2} GiB available.");
        var transfer = new Transfer(progress, quant.TotalBytes, parts.Count);
        long done = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (file, uri, path, final) = parts[i];
            if (!final) await DownloadPartAsync(file, uri, path, done, i + 1, transfer, token);
            done += file.Size;
        }
        transfer.Report(done, parts.Count);
        return parts[0].Path;
    }

    public void DeletePartial(string repoId, HfQuantization quant)
    {
        foreach (var file in quant.Parts)
        {
            var path = Destination(repoId, file); CheckNoLinks(path); CheckNoLinks(path + PartSuffix);
            File.Delete(path + PartSuffix);
        }
    }

    private async Task DownloadPartAsync(HfFile file, Uri uri, string path, long before, int part, Transfer transfer, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partPath = path + PartSuffix;
        CheckNoLinks(path); CheckNoLinks(partPath); // Parts after the first are created long after the up-front check.
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing > file.Size) { File.Delete(partPath); existing = 0; }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (existing > 0) await HashExistingAsync(partPath, hash, token);
        transfer.Report(before + existing, part);
        var verified = existing == file.Size ? Matches(hash, file) : await ReceiveAsync(file, uri, partPath, existing, hash, before, part, transfer, token);
        if (!verified) { File.Delete(partPath); throw new InvalidDataException(Corrupted); }
        CheckNoLinks(path); CheckNoLinks(partPath);
        File.Move(partPath, path);
    }

    // Returns false when the server's answer or the received bytes do not describe the expected file; the caller then discards the .part.
    private async Task<bool> ReceiveAsync(HfFile file, Uri uri, string partPath, long existing, IncrementalHash hash, long before, int part, Transfer transfer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LLamaModelLoader/1.0");
        request.Headers.AcceptEncoding.ParseAdd("identity"); // A compressed body would not line up with the byte offsets.
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((response.RequestMessage?.RequestUri ?? uri).Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Download redirected to an insecure address.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Hugging Face refused the request (rate limit). Try again later.", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0) return false;
        response.EnsureSuccessStatusCode();
        var resume = response.StatusCode == HttpStatusCode.PartialContent;
        if (resume)
        {
            // Appending is only safe when the server continues exactly where the .part ends.
            if (existing == 0 || response.Content.Headers.ContentRange is not { From: { } from } range || from != existing ||
                (range.To is { } to && to != file.Size - 1) || (range.Length is { } length && length != file.Size)) return false;
        }
        else
        {
            if (response.StatusCode != HttpStatusCode.OK) return false;
            existing = 0; hash.GetHashAndReset(); // The server ignored Range and sends the whole file again.
        }
        if (response.Content.Headers.ContentLength is { } declared && declared != file.Size - existing) return false;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(partPath, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024]; var received = existing;
        var lastProgress = Environment.TickCount64;
        while (true)
        {
            var count = await source.ReadAsync(buffer, token);
            if (count == 0) break;
            received += count;
            if (received > file.Size) return false;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            transfer.Fetched += count;
            if (Environment.TickCount64 - lastProgress >= 150)
            {
                transfer.Report(before + received, part);
                lastProgress = Environment.TickCount64;
            }
        }
        return received == file.Size && Matches(hash, file);
    }

    private void CheckNoLinks(string path) => SafePaths.CheckNoReparsePointsBelow(path, _root);

    private static bool Matches(IncrementalHash hash, HfFile file) => Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task HashExistingAsync(string partPath, IncrementalHash hash, CancellationToken token)
    {
        await using var input = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var buffer = new byte[128 * 1024]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0) hash.AppendData(buffer, 0, count);
    }

    private enum PartStatus { Missing, Final, Conflict }

    // A final name must hold exactly the expected bytes; anything else there is never overwritten. An oversized .part counts as nothing because the download discards it.
    private static (PartStatus Status, long PartBytes) Inspect(string path, HfFile file)
    {
        if (Directory.Exists(path)) return (PartStatus.Conflict, 0);
        if (File.Exists(path)) return (new FileInfo(path).Length == file.Size ? PartStatus.Final : PartStatus.Conflict, 0);
        var length = File.Exists(path + PartSuffix) ? new FileInfo(path + PartSuffix).Length : 0;
        return (PartStatus.Missing, length <= file.Size ? length : 0);
    }

    private static long AvailableFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (ArgumentException) { return long.MaxValue; } // A network share has no drive letter to ask.
    }

    private sealed class Transfer(IProgress<DownloadProgress>? progress, long total, int parts)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public long Fetched;
        public void Report(long downloaded, int part) =>
            progress?.Report(new(downloaded, total, part, parts, Fetched / Math.Max(_clock.Elapsed.TotalSeconds, 0.001)));
    }
}
