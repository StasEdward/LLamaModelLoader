using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Stages verified archives, probes the server, then promotes a complete side-by-side installation.</summary>
public sealed class ServerInstaller
{
    private const string ManifestName = "installation.json";
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<ServerCapabilities>> _probe;
    public ServerInstaller(string root, HttpClient http, Func<string, CancellationToken, Task<ServerCapabilities>>? probe = null)
    {
        _root = Path.GetFullPath(root); _http = http;
        _probe = probe ?? new ServerProbe().InspectAsync;
    }

    public IReadOnlyList<InstalledServer> GetInstalled()
    {
        if (!Directory.Exists(_root)) return [];
        CheckPath(_root);
        var result = new List<InstalledServer>();
        foreach (var directory in Directory.EnumerateDirectories(_root).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            try
            {
                CheckPath(directory);
                var manifestPath = Path.Combine(directory, ManifestName); CheckPath(manifestPath);
                if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 128 * 1024) continue;
                var manifest = JsonSerializer.Deserialize<InstalledServer>(File.ReadAllText(manifestPath));
                if (manifest?.Build is null || Path.GetFileName(directory) != manifest.Build.DirectoryName) continue;
                ServerReleaseClient.Validate(manifest.Build);
                var exe = Path.Combine(directory, "llama-server.exe"); CheckPath(exe);
                if (File.Exists(exe)) result.Add(manifest with { ExecutablePath = exe });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return result.OrderByDescending(i => i.InstalledAt).ToArray();
    }

    public async Task<InstalledServer> InstallAsync(ServerBuild build, IProgress<InstallationProgress>? progress = null, CancellationToken token = default)
    {
        ServerReleaseClient.Validate(build);
        CheckPath(_root); Directory.CreateDirectory(_root);
        var lockPath = Path.Combine(_root, ".install.lock"); CheckPath(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var destination = Child(build.DirectoryName);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("This build already exists. Select it in Installed builds.");
        var staging = Child(".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var payload = Path.Combine(staging, "payload"); Directory.CreateDirectory(payload);
            long downloaded = 0;
            foreach (var asset in build.Assets)
            {
                token.ThrowIfCancellationRequested();
                var archive = Path.Combine(staging, asset.Name);
                progress?.Report(new($"Downloading {asset.Name}", downloaded, build.DownloadBytes));
                await DownloadAsync(asset, archive, downloaded, build.DownloadBytes, progress, token);
                downloaded += asset.Size;
                progress?.Report(new($"Extracting {asset.Name}", downloaded, build.DownloadBytes));
                await Task.Run(() => Extract(archive, payload, token), token);
            }
            var exe = Path.Combine(payload, "llama-server.exe");
            if (!File.Exists(exe)) throw new InvalidDataException("The archive has no llama-server.exe at its root. Its layout is unsupported.");
            progress?.Report(new("Checking server version and supported options…", downloaded, build.DownloadBytes));
            var capabilities = await _probe(exe, token);
            var installed = new InstalledServer(build, DateTimeOffset.UtcNow, capabilities.Version, Path.Combine(destination, "llama-server.exe"));
            await File.WriteAllTextAsync(Path.Combine(payload, ManifestName), JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true }), token);
            token.ThrowIfCancellationRequested();
            CheckPath(destination); CheckPath(payload);
            Directory.Move(payload, destination);
            progress?.Report(new("Installed. Select Use selected build, then save Settings.", downloaded, build.DownloadBytes));
            return installed;
        }
        finally
        {
            // Delete only this operation's known staging directory, never an installed or external server.
            CheckPath(staging);
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { /* A locked temporary file can be removed after the application exits. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task DownloadAsync(ReleaseAsset asset, string path, long previous, long total, IProgress<InstallationProgress>? progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("LLamaModelLoader/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("Download size differs from the release metadata.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; long received = 0;
        var lastProgress = Environment.TickCount64;
        while (true)
        {
            var count = await source.ReadAsync(buffer, token);
            if (count == 0) break;
            received += count;
            if (received > asset.Size) throw new InvalidDataException("Download exceeds the declared archive size.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (Environment.TickCount64 - lastProgress >= 150)
            {
                progress?.Report(new($"Downloading {asset.Name}", previous + received, total));
                lastProgress = Environment.TickCount64;
            }
        }
        if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive size or SHA-256 verification failed. Nothing was installed.");
    }

    private static void Extract(string archivePath, string target, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("Too many archive entries.");
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (name.Length == 0 || parts.Any(UnsafePart) || kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException($"Unsafe archive entry: {name}");
            var path = Path.GetFullPath(Path.Combine(target, Path.Combine(parts)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(path) || parts[0].Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid or duplicate archive entry: {name}");
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            expanded = checked(expanded + entry.Length);
            if (expanded > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded installation exceeds 8 GiB.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Never overwrite files from another archive. Matching CUDA companions contain separate runtime DLLs.
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[128 * 1024]; long written = 0;
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested(); written += count;
                if (written > entry.Length) throw new InvalidDataException("An archive entry exceeds its declared size.");
                output.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated archive entry.");
        }
    }

    private static bool UnsafePart(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ');
        return part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3])));
    }

    private string Child(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation path is outside the managed directory.");
        CheckPath(path); return path;
    }

    private static void CheckPath(string path)
    {
        // Refuse junctions/symlinks anywhere in the path before writing, moving, or cleaning staging.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Managed installation paths must not contain symbolic links or junctions.");
    }
}
