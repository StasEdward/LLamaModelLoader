using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private const string Corrupted = "The downloaded file is corrupted (size or SHA-256 mismatch). Download it again.";
    private const long Margin = 256L << 20;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llama-model-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Sha = new('a', 40);
    private static readonly byte[] First = Data(300, 1), Second = Data(200, 2);
    private static readonly HfFile Shard1 = Remote("Q8_0/M-Q8_0-00001-of-00002.gguf", First), Shard2 = Remote("Q8_0/M-Q8_0-00002-of-00002.gguf", Second);
    private static readonly HfQuantization Sharded = new("Q8_0", [Shard1, Shard2]);

    private static byte[] Data(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();
    private static HfFile Remote(string path, byte[] data) => new(path, data.Length, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
    private static HfQuantization Single(HfFile file) => new("Q4_K_M", [file]);
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static Task<HttpResponseMessage> Reply(HttpResponseMessage response) => Task.FromResult(response);
    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    private static HttpResponseMessage Partial(byte[] data, int from) => new(HttpStatusCode.PartialContent)
    {
        Content = new ByteArrayContent(data[from..]) { Headers = { ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length) } }
    };
    private static HttpResponseMessage Streamed(Stream body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) };
    private ModelDownloader Downloader(HttpClient http, Func<string, long>? freeSpace = null, TimeSpan? idleTimeout = null) => new(_root, http, freeSpace ?? (_ => long.MaxValue), idleTimeout);
    private string Dest(HfFile file) => Downloader(Http((_, _) => throw new Exception())).Destination("o/r", file);
    private string[] Parts() => Directory.Exists(_root) ? Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories) : [];
    private static string Write(string path, byte[] data) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, data); return path; }

    [Fact]
    public async Task DownloadsVerifiesAndMovesIntoRepoFolder()
    {
        var requests = new List<string>();
        using var http = Http((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Range);
            Assert.Equal("LLamaModelLoader/1.0", request.Headers.UserAgent.ToString());
            return Reply(Ok(request.RequestUri.AbsolutePath.Contains("00001") ? First : Second));
        });
        var progress = new Sink();
        var first = await Downloader(http).DownloadAsync("o/r", Sha, Sharded, progress, default);
        Assert.Equal(Path.Combine(_root, "o", "r", "Q8_0", "M-Q8_0-00001-of-00002.gguf"), first);
        Assert.Equal(First, File.ReadAllBytes(first));
        Assert.Equal(Second, File.ReadAllBytes(Path.Combine(_root, "o", "r", "Q8_0", "M-Q8_0-00002-of-00002.gguf")));
        Assert.Empty(Parts());
        Assert.Equal([$"https://huggingface.co/o/r/resolve/{Sha}/Q8_0/M-Q8_0-00001-of-00002.gguf", $"https://huggingface.co/o/r/resolve/{Sha}/Q8_0/M-Q8_0-00002-of-00002.gguf"], requests);
        var last = progress.Reports[^1];
        Assert.Equal((500L, 500L, 2, 2), (last.DownloadedBytes, last.TotalBytes, last.Part, last.Parts));
        Assert.Equal(1, progress.Reports[0].Part);
    }

    [Fact]
    public async Task ResumesFromPartWith206AndVerifiesWholeFile()
    {
        var file = Remote("m.gguf", First); var dest = Dest(file);
        Write(dest + ".part", First[..100]);
        using var http = Http((request, _) =>
        {
            Assert.Equal("bytes=100-", request.Headers.Range!.ToString());
            return Reply(Partial(First, 100));
        });
        await Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default);
        Assert.Equal(First, File.ReadAllBytes(dest));
        Assert.Empty(Parts());
    }

    [Fact]
    public async Task RestartsWhenServerIgnoresRange()
    {
        var file = Remote("m.gguf", First); var dest = Dest(file);
        Write(dest + ".part", First[..100]);
        var asked = false;
        using var http = Http((request, _) => { asked = request.Headers.Range is not null; return Reply(Ok(First)); });
        await Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default);
        Assert.True(asked);
        Assert.Equal(First, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task DiscardsOversizedPart()
    {
        var file = Remote("m.gguf", First); var dest = Dest(file);
        Write(dest + ".part", Data(First.Length + 50, 9));
        using var http = Http((request, _) => { Assert.Null(request.Headers.Range); return Reply(Ok(First)); });
        await Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default);
        Assert.Equal(First, File.ReadAllBytes(dest));
        Assert.Empty(Parts());
    }

    [Fact]
    public async Task VerifiesAndFinishesCompletePartWithoutRequest()
    {
        var file = Remote("m.gguf", First); var dest = Dest(file);
        Write(dest + ".part", First);
        using var http = Http((_, _) => throw new Exception("No request expected"));
        await Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default);
        Assert.Equal(First, File.ReadAllBytes(dest));
        Assert.Empty(Parts());
    }

    [Fact]
    public async Task ShaMismatchDeletesPartAndThrows()
    {
        var file = Shard1 with { Sha256 = new string('0', 64) }; var dest = Dest(file);
        using var http = Http((_, _) => Reply(Ok(First)));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default));
        Assert.Equal(Corrupted, ex.Message);
        Assert.Empty(Parts()); Assert.False(File.Exists(dest));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SizeMismatchThrows(bool declaredLength)
    {
        byte[] longer = [.. First, 7];
        using var http = Http((_, _) => Reply(declaredLength ? Ok(longer) : Streamed(new Body(longer))));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal(Corrupted, ex.Message);
        Assert.Empty(Parts()); Assert.False(File.Exists(Dest(Shard1)));
    }

    [Fact]
    public async Task ShortBodyThrows()
    {
        using var http = Http((_, _) => Reply(Streamed(new Body(First[..299]))));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal(Corrupted, ex.Message);
        Assert.Empty(Parts());
    }

    [Fact]
    public async Task CancellationKeepsPart()
    {
        var body = new Body(First, stallAfter: 150); using var cancellation = new CancellationTokenSource();
        using var http = Http((_, _) => Reply(Streamed(body)));
        var download = Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, cancellation.Token);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        var part = Assert.Single(Parts());
        Assert.Equal(150, new FileInfo(part).Length);
        Assert.False(File.Exists(Dest(Shard1)));
    }

    [Fact]
    public async Task StalledBodyThrowsAndKeepsReceivedBytes()
    {
        var body = new Body(First, stallAfter: 150);
        using var http = Http((_, _) => Reply(Streamed(body)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(http, idleTimeout: TimeSpan.FromMilliseconds(200)).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal("The download stalled. Resume to continue.", ex.Message);
        Assert.Equal(150, new FileInfo(Assert.Single(Parts())).Length);
        Assert.False(File.Exists(Dest(Shard1)));
    }

    [Fact]
    public async Task StalledResponseHeadersThrow()
    {
        using var http = Http(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Ok(First); });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(http, idleTimeout: TimeSpan.FromMilliseconds(200)).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal("The download stalled. Resume to continue.", ex.Message);
        Assert.False(File.Exists(Dest(Shard1)));
    }

    [Fact]
    public void TransferRateUsesOnlyTheLastFiveSeconds()
    {
        var rate = new TransferRate();
        Assert.Equal(0, rate.BytesPerSecond(TimeSpan.Zero));
        rate.Add(1000, TimeSpan.FromSeconds(1));
        Assert.Equal(500, rate.BytesPerSecond(TimeSpan.FromSeconds(2))); // A young transfer divides by its own duration.
        for (var second = 3; second <= 10; second++) rate.Add(1000, TimeSpan.FromSeconds(second));
        Assert.Equal(1000, rate.BytesPerSecond(TimeSpan.FromSeconds(10)));
        Assert.Equal(400, rate.BytesPerSecond(TimeSpan.FromSeconds(13))); // Stalled since 10 s: only the samples at 9 and 10 s remain.
        Assert.Equal(0, rate.BytesPerSecond(TimeSpan.FromSeconds(16)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "The file or repository was not found or is not public.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Hugging Face refused the request (rate limit). Try again later.")]
    public async Task ReportsRefusedRequests(HttpStatusCode status, string message)
    {
        using var http = Http((_, _) => Reply(new HttpResponseMessage(status)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal(message, ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task InsufficientSpaceFailsBeforeRequest()
    {
        var required = Sharded.TotalBytes + Margin; var asked = false;
        using var http = Http((_, _) => throw new Exception("No request expected"));
        var downloader = Downloader(http, path => { Assert.Equal(_root, path); asked = true; return required - 1; });
        var ex = await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync("o/r", Sha, Sharded, null, default));
        Assert.Contains($"{required / 1073741824d:N2} GiB required", ex.Message);
        Assert.Contains($"{(required - 1) / 1073741824d:N2} GiB available", ex.Message);
        Assert.True(asked); Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task FreeSpaceCountsExistingPartBytesAndCompleteParts()
    {
        var dest = Dest(Shard1);
        Write(dest + ".part", First[..100]);
        Write(Dest(Shard2), Second);
        using var http = Http((_, _) => Reply(Partial(First, 100)));
        // Only the 200 missing bytes of the first shard are still needed.
        await Downloader(http, _ => 200 + Margin).DownloadAsync("o/r", Sha, Sharded, null, default);
        Assert.Equal(First, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task SkipsCompletePartsAndReportsState()
    {
        var downloader = Downloader(Http((_, _) => throw new Exception("Unused")));
        Assert.Equal((LocalModelState.NotDownloaded, 0L, (string?)null), downloader.GetState("o/r", Sharded));
        Write(downloader.Destination("o/r", Shard1), First);
        Assert.Equal((LocalModelState.Partial, 300L, (string?)null), downloader.GetState("o/r", Sharded));
        var requests = new List<string>();
        using var http = Http((request, _) => { requests.Add(request.RequestUri!.AbsolutePath); return Reply(Ok(Second)); });
        downloader = Downloader(http);
        await downloader.DownloadAsync("o/r", Sha, Sharded, null, default);
        Assert.Equal([$"/o/r/resolve/{Sha}/Q8_0/M-Q8_0-00002-of-00002.gguf"], requests);
        Assert.Equal((LocalModelState.Downloaded, 500L, (string?)null), downloader.GetState("o/r", Sharded));
        File.WriteAllBytes(downloader.Destination("o/r", Shard2), Second[..10]);
        Assert.Equal((LocalModelState.Conflict, 0L, downloader.Destination("o/r", Shard2)), downloader.GetState("o/r", Sharded));
        File.Delete(downloader.Destination("o/r", Shard2)); Directory.CreateDirectory(downloader.Destination("o/r", Shard2));
        Assert.Equal(downloader.Destination("o/r", Shard2), downloader.GetState("o/r", Sharded).ConflictPath);
    }

    [Fact]
    public void ReportsPartialBytesFromPartFiles()
    {
        var downloader = Downloader(Http((_, _) => throw new Exception("Unused")));
        Write(downloader.Destination("o/r", Shard2) + ".part", Second[..40]);
        Assert.Equal((LocalModelState.Partial, 40L, (string?)null), downloader.GetState("o/r", Sharded));
    }

    [Fact]
    public async Task ConflictingFinalFileIsNeverOverwritten()
    {
        var dest = Dest(Shard1); Write(dest, [1, 2, 3]);
        using var http = Http((_, _) => throw new Exception("No request expected"));
        var ex = await Assert.ThrowsAsync<IOException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal($"A different file already exists at {dest}.", ex.Message);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task RejectsInsecureRedirect()
    {
        using var http = Http((_, _) => Reply(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(First), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.example.com/x") }));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal("Download redirected to an insecure address.", ex.Message);
        Assert.Empty(Parts());
    }

    [Fact]
    public async Task ResumeWithMisalignedContentRangeDiscardsPart()
    {
        var file = Remote("m.gguf", First); var dest = Dest(file);
        Write(dest + ".part", First[..100]);
        using var http = Http((_, _) => Reply(Partial(First, 50)));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default));
        Assert.Equal(Corrupted, ex.Message);
        Assert.Empty(Parts()); Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task ContentLengthMustMatchRequest()
    {
        var file = Remote("m.gguf", First); var dest = Dest(file);
        Write(dest + ".part", First[..100]);
        // A 206 that declares the whole file's length instead of the 200 remaining bytes.
        using var http = Http((_, _) => Reply(new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(First) { Headers = { ContentRange = new ContentRangeHeaderValue(100, 299, 300) } } }));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(file), null, default));
        Assert.Equal(Corrupted, ex.Message);
        Assert.Empty(Parts());
    }

    [Fact]
    public void DeletePartialRemovesOnlyPartFiles()
    {
        var downloader = Downloader(Http((_, _) => throw new Exception("Unused")));
        var final1 = Write(downloader.Destination("o/r", Shard1), First);
        var part2 = Write(downloader.Destination("o/r", Shard2) + ".part", Second[..40]);
        downloader.DeletePartial("o/r", Sharded);
        Assert.False(File.Exists(part2)); Assert.True(File.Exists(final1));
        downloader.DeletePartial("o/r", Sharded);
    }

    [Theory]
    [InlineData("../x.gguf")]
    [InlineData("a/../x.gguf")]
    [InlineData("CON.gguf")]
    [InlineData("Q8_0/NUL.gguf")]
    [InlineData("/x.gguf")]
    [InlineData("C:/x.gguf")]
    [InlineData("a\\b.gguf")]
    [InlineData("model.bin")]
    public void RejectsPathsOutsideModelsFolder(string path)
    {
        var downloader = Downloader(Http((_, _) => throw new Exception("Unused")));
        Assert.Throws<ArgumentException>(() => downloader.Destination("o/r", new HfFile(path, 1, new string('0', 64))));
    }

    [Theory]
    [InlineData("con/x")]
    [InlineData("o/r.")]
    [InlineData("o/r/s")]
    [InlineData("../x")]
    [InlineData("nul/nul")]
    public void RejectsUnsafeRepositoryIds(string repoId)
    {
        var downloader = Downloader(Http((_, _) => throw new Exception("Unused")));
        Assert.Throws<ArgumentException>(() => downloader.Destination(repoId, Shard1));
    }

    [Fact]
    public async Task RejectsMalformedRequestsBeforeAnyRequest()
    {
        using var http = Http((_, _) => throw new Exception("No request expected"));
        var downloader = Downloader(http);
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync("o/r", "main", Sharded, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync("o/r", Sha, Single(Shard1 with { Sha256 = "xyz" }), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync("o/r", Sha, new("Q8_0", []), null, default));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void RequiresFullyQualifiedModelsDirectory()
    {
        using var http = Http((_, _) => throw new Exception("Unused"));
        Assert.Throws<ArgumentException>(() => new ModelDownloader("models", http));
        Assert.Throws<ArgumentException>(() => new ModelDownloader(@"\models", http));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.StandardOutput.ReadToEnd(); mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public async Task RefusesDestinationBehindJunction()
    {
        var elsewhere = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(elsewhere);
        var models = Path.Combine(_root, "models"); Directory.CreateDirectory(Path.Combine(models, "o"));
        Junction(Path.Combine(models, "o", "r"), elsewhere);
        using var http = Http((_, _) => throw new Exception("No request expected"));
        var downloader = new ModelDownloader(models, http, _ => long.MaxValue);
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Throws<IOException>(() => downloader.DeletePartial("o/r", Single(Shard1)));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task DownloadsIntoModelsFolderThatIsItselfAJunction()
    {
        var real = Path.Combine(_root, "real"); Directory.CreateDirectory(real);
        var models = Path.Combine(_root, "models"); Junction(models, real);
        using var http = Http((_, _) => Reply(Ok(First)));
        var downloader = new ModelDownloader(models, http, _ => long.MaxValue);
        var path = await downloader.DownloadAsync("o/r", Sha, Single(Shard1), null, default);
        Assert.Equal(First, File.ReadAllBytes(path));
        Assert.Equal(First, File.ReadAllBytes(Path.Combine(real, "o", "r", "Q8_0", "M-Q8_0-00001-of-00002.gguf")));
        Assert.Equal(LocalModelState.Downloaded, downloader.GetState("o/r", Single(Shard1)).State);
        downloader.DeletePartial("o/r", Single(Shard1));
    }

    [Fact]
    public async Task RefusesPartSiblingThatIsALink()
    {
        // A junction stands in for the link, since creating file symlinks needs a privilege.
        var elsewhere = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(elsewhere);
        var dest = Dest(Shard1); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Junction(dest + ".part", elsewhere);
        using var http = Http((_, _) => throw new Exception("No request expected"));
        await Assert.ThrowsAsync<IOException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task RefusesPartFileThatIsASymlink()
    {
        var outside = Path.Combine(_root, "outside.bin"); Write(outside, [9, 9, 9]);
        var dest = Dest(Shard1); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try { File.CreateSymbolicLink(dest + ".part", outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // Creating symlinks needs a privilege this machine does not grant.
        using var http = Http((_, _) => throw new Exception("No request expected"));
        await Assert.ThrowsAsync<IOException>(() => Downloader(http).DownloadAsync("o/r", Sha, Single(Shard1), null, default));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(outside));
        Assert.False(File.Exists(dest));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // Remove junctions first so deleting the tree cannot follow them.
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).ToArray())
            if (Directory.Exists(directory) && new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(directory);
        Directory.Delete(_root, true);
    }

    private sealed class Sink : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    // Forward-only body so the response has no Content-Length; optionally stalls after a prefix until cancelled.
    private sealed class Body(byte[] data, int stallAfter = int.MaxValue) : Stream
    {
        private int _position;
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position >= stallAfter && _position < data.Length)
            {
                Stalled.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            var count = Math.Min(buffer.Length, Math.Min(stallAfter, data.Length) - _position);
            if (count <= 0) return 0;
            data.AsMemory(_position, count).CopyTo(buffer); _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
