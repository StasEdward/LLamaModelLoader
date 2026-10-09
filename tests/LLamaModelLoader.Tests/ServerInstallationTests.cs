using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class ServerInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llama-install-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly ServerCapabilities Capabilities = new("test version", ["--model", "--port"]);
    private static byte[] Zip(params (string Name, string Content, int Attributes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, content, attributes) in entries)
            {
                var entry = archive.CreateEntry(name); entry.ExternalAttributes = attributes;
                using var writer = new StreamWriter(entry.Open()); writer.Write(content);
            }
        return memory.ToArray();
    }
    private static ServerBuild Build(byte[] zip, string tag = "b100", string backend = "cpu")
    {
        var name = $"llama-{tag}-bin-win-{backend}-x64.zip";
        return new(tag, DateTimeOffset.Parse("2026-10-10T00:00:00Z"), true, backend,
            [new(name, $"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{name}", zip.Length, "sha256:" + Convert.ToHexString(SHA256.HashData(zip)))]);
    }
    private static object Release(ServerBuild build, bool draft = false) => new
    {
        tag_name = build.Tag, published_at = build.PublishedAt, prerelease = build.Prerelease, draft,
        assets = build.Assets.Select(a => new { name = a.Name, browser_download_url = a.DownloadUrl, size = a.Size, digest = a.Digest })
    };
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpClient Http(byte[] zip) => Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));
    private ServerInstaller Installer(HttpClient http, Func<string, CancellationToken, Task<ServerCapabilities>>? probe = null) =>
        new(_root, http, probe ?? ((_, _) => Task.FromResult(Capabilities)));
    private void AssertNoInstall(ServerInstaller installer)
    {
        Assert.Empty(installer.GetInstalled());
        if (Directory.Exists(_root)) Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void CatalogIncludesRollingBuildsAndPairsExactCudaRuntime()
    {
        var zip = Zip(("llama-server.exe", "server", 0));
        var cpu = Build(zip); var cuda = Build(zip, backend: "cuda-13.4");
        var runtime = cuda.Assets[0] with { Name = "cudart-llama-bin-win-cuda-13.4-x64.zip", DownloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b100/cudart-llama-bin-win-cuda-13.4-x64.zip" };
        var mixed = cpu with { Assets = [.. cpu.Assets, .. cuda.Assets, runtime] };
        var sourceOnly = cpu with { Tag = "v0.6.0", Assets = [] };
        var builds = ServerReleaseClient.Parse(JsonSerializer.Serialize(new[] { Release(sourceOnly), Release(mixed), Release(cpu with { Tag = "draft" }, true) }));
        Assert.Equal(2, builds.Count);
        Assert.True(builds[0].Prerelease);
        Assert.Equal(runtime, builds.Single(b => b.Backend == "cuda-13.4").Assets[1]);
        Assert.Equal(2 * zip.Length, builds.Single(b => b.Backend == "cuda-13.4").DownloadBytes);
    }

    [Fact]
    public void CatalogRejectsCudaWithoutMatchingRuntimeAndUnknownChecksums()
    {
        var zip = Zip(("llama-server.exe", "server", 0)); var cuda = Build(zip, backend: "cuda-13.4");
        var oldRuntime = cuda.Assets[0] with { Name = "cudart-llama-bin-win-cuda-12.4-x64.zip" };
        var noHash = Build(zip); noHash = noHash with { Assets = [noHash.Assets[0] with { Digest = "" }] };
        Assert.Empty(ServerReleaseClient.Parse(JsonSerializer.Serialize(new[] { Release(cuda with { Assets = [.. cuda.Assets, oldRuntime] }), Release(noHash) })));
    }

    [Fact]
    public async Task InstallsSideBySideAndProbesBeforePromotion()
    {
        var zip = Zip(("llama-server.exe", "server", 0), ("ggml.dll", "library", 0)); using var http = Http(zip);
        var probes = 0;
        var installer = Installer(http, (path, _) =>
        {
            Assert.Contains(".staging-", path); Assert.Equal("library", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "ggml.dll")));
            probes++; return Task.FromResult(Capabilities);
        });
        var first = await installer.InstallAsync(Build(zip));
        var second = await installer.InstallAsync(Build(zip, "b101"));
        Assert.Equal(2, probes); Assert.Equal(2, installer.GetInstalled().Count);
        Assert.True(File.Exists(first.ExecutablePath)); Assert.True(File.Exists(second.ExecutablePath));
        Assert.DoesNotContain(Directory.GetDirectories(_root), d => Path.GetFileName(d).StartsWith('.'));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(Build(zip)));
        Assert.Equal("server", File.ReadAllText(first.ExecutablePath));
    }

    [Fact]
    public async Task ExtractsMatchingCudaRuntimeNextToServer()
    {
        var zip = Zip(("llama-server.exe", "server", 0)); var runtimeZip = Zip(("cudart64_13.dll", "runtime", 0));
        var build = Build(zip, backend: "cuda-13.4"); var runtimeName = "cudart-llama-bin-win-cuda-13.4-x64.zip";
        build = build with { Assets = [.. build.Assets, new(runtimeName, $"https://github.com/ggml-org/llama.cpp/releases/download/b100/{runtimeName}", runtimeZip.Length, "sha256:" + Convert.ToHexString(SHA256.HashData(runtimeZip)))] };
        using var http = Http((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.Contains("cudart-") ? runtimeZip : zip) }));
        var installed = await Installer(http, (path, _) =>
        {
            Assert.Equal("runtime", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "cudart64_13.dll")));
            return Task.FromResult(Capabilities);
        }).InstallAsync(build);
        Assert.True(File.Exists(installed.ExecutablePath));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("..\\escape.exe")]
    [InlineData("/escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("file:stream")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("dir./file")]
    [InlineData("installation.json")]
    public async Task RejectsUnsafeArchivePaths(string name)
    {
        var zip = Zip(("llama-server.exe", "server", 0), (name, "bad", 0)); using var http = Http(zip);
        var installer = Installer(http, (_, _) => throw new Exception("Unsafe archive was executed"));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Build(zip)));
        AssertNoInstall(installer);
    }

    [Theory]
    [InlineData(0xA000 << 16)]
    [InlineData(0x400)]
    public async Task RejectsSymbolicLinksAndReparseEntries(int attributes)
    {
        var zip = Zip(("llama-server.exe", "server", 0), ("link", "target", attributes)); using var http = Http(zip); var installer = Installer(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Build(zip))); AssertNoInstall(installer);
    }

    [Fact]
    public async Task RejectsCaseInsensitiveDuplicates()
    {
        var zip = Zip(("llama-server.exe", "server", 0), ("LLAMA-SERVER.EXE", "other", 0)); using var http = Http(zip); var installer = Installer(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Build(zip))); AssertNoInstall(installer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsHashOrSizeMismatchBeforeExecuting(bool hashMismatch)
    {
        var zip = Zip(("llama-server.exe", "server", 0)); var build = Build(zip);
        build = build with { Assets = [hashMismatch ? build.Assets[0] with { Digest = "sha256:" + new string('0', 64) } : build.Assets[0] with { Size = zip.Length + 1 }] };
        using var http = Http(zip); var installer = Installer(http, (_, _) => throw new Exception("Unverified archive was executed"));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(build)); AssertNoInstall(installer);
    }

    [Fact]
    public async Task FailedProbePreservesPreviousInstallation()
    {
        var zip = Zip(("llama-server.exe", "server", 0)); using var http = Http(zip);
        var existing = await Installer(http).InstallAsync(Build(zip));
        var failing = Installer(http, (_, _) => throw new InvalidOperationException("Missing runtime"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.InstallAsync(Build(zip, "b101")));
        Assert.Single(failing.GetInstalled()); Assert.True(File.Exists(existing.ExecutablePath));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task CancelDuringDownloadRemovesStagingAndReleasesLock()
    {
        var zip = Zip(("llama-server.exe", "server", 0)); using var cancellation = new CancellationTokenSource();
        using var http = Http(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); throw new Exception(); });
        var installer = Installer(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Build(zip), token: cancellation.Token)); AssertNoInstall(installer);
        using var retryHttp = Http(zip); await Installer(retryHttp).InstallAsync(Build(zip));
    }

    [Fact]
    public async Task CancellationAfterProbeDoesNotPromoteInstallation()
    {
        var zip = Zip(("llama-server.exe", "server", 0)); using var http = Http(zip); using var cancellation = new CancellationTokenSource();
        var installer = Installer(http, (_, _) => { cancellation.Cancel(); return Task.FromResult(Capabilities); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Build(zip), token: cancellation.Token)); AssertNoInstall(installer);
    }

    [Fact]
    public async Task RejectsUntrustedDownloadSourceBeforeMakingRequest()
    {
        var zip = Zip(("llama-server.exe", "server", 0)); var build = Build(zip);
        build = build with { Assets = [build.Assets[0] with { DownloadUrl = "https://example.com/server.zip" }] };
        using var http = Http((_, _) => throw new Exception("Untrusted URL was requested")); var installer = Installer(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(build)); AssertNoInstall(installer);
    }

    [Fact]
    public void CorruptOrIncompleteInstallationsAreNotListed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "b100-cpu")); Directory.CreateDirectory(Path.Combine(_root, "b101-cpu"));
        File.WriteAllText(Path.Combine(_root, "b100-cpu", "installation.json"), "{broken");
        File.WriteAllText(Path.Combine(_root, "b101-cpu", "installation.json"), "{\"Build\":null}");
        using var http = Http([]); Assert.Empty(Installer(http).GetInstalled());
    }

    [Fact]
    public async Task RateLimitHasActionableMessage()
    {
        using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new ServerReleaseClient(http).GetBuildsAsync());
        Assert.Contains("Try again later", ex.Message);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
