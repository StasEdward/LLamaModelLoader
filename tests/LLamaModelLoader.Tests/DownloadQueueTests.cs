using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class DownloadQueueTests : IDisposable
{
    private const string Corrupted = "The downloaded file is corrupted (size or SHA-256 mismatch). Download it again.";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llama-queue-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Sha = new('a', 40);
    private static readonly byte[] First = Data(300, 1), Second = Data(200, 2);
    private static readonly HfFile FileA = Remote("a.gguf", First), FileB = Remote("b.gguf", Second);

    public DownloadQueueTests() => Directory.CreateDirectory(_root);

    private static byte[] Data(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();
    private static HfFile Remote(string path, byte[] data) => new(path, data.Length, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
    private static HfQuantization Quant(HfFile file, string label = "Q4_K_M") => new(label, [file]);
    private static HttpClient Http(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static Task<HttpResponseMessage> Reply(HttpContent content, HttpStatusCode status = HttpStatusCode.OK) => Task.FromResult(new HttpResponseMessage(status) { Content = content });
    private DownloadQueue Queue(HttpClient http) => new(() => _root, http, _ => long.MaxValue);
    private DownloadItem Add(DownloadQueue queue, string repo, HfFile file, string label = "Q4_K_M") => queue.Enqueue(repo, Sha, Quant(file, label), 4096, "f16", null);
    private string Part(string repo, HfFile file) => Path.Combine(_root, Path.Combine(repo.Split('/')), file.Path) + ".part";
    private string Final(string repo, HfFile file) => Path.Combine(_root, Path.Combine(repo.Split('/')), file.Path);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task RunsOneDownloadAtATimeInOrder()
    {
        var bodyA = new Body(First, stallAfter: 0); var requests = new List<string>();
        using var http = Http(request =>
        {
            lock (requests) requests.Add(request.RequestUri!.AbsolutePath);
            return Reply(request.RequestUri.AbsolutePath.Contains("/o/a/") ? new StreamContent(bodyA) : new StreamContent(new Body(Second)));
        });
        await using var queue = Queue(http); var changes = 0;
        queue.Changed += _ => Interlocked.Increment(ref changes);
        var a = Add(queue, "o/a", FileA); var b = Add(queue, "o/b", FileB);
        await bodyA.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadItemState.Downloading, a.State); Assert.Equal(DownloadItemState.Queued, b.State);
        Assert.True(queue.IsActive);
        lock (requests) Assert.Equal([$"/o/a/resolve/{Sha}/a.gguf"], requests);
        bodyA.Release.SetResult();
        await Until(() => a.State == DownloadItemState.Completed && b.State == DownloadItemState.Completed);
        Assert.Equal(Final("o/a", FileA), a.FinalPath); Assert.Equal(Final("o/b", FileB), b.FinalPath);
        Assert.Equal(First, File.ReadAllBytes(a.FinalPath!)); Assert.Equal(Second, File.ReadAllBytes(b.FinalPath!));
        Assert.Equal("Downloaded.", a.Message);
        lock (requests) Assert.Equal([$"/o/a/resolve/{Sha}/a.gguf", $"/o/b/resolve/{Sha}/b.gguf"], requests);
        Assert.True(changes > 0);
        await Until(() => !queue.IsActive);
        Assert.Equal([a, b], queue.Items);
    }

    [Fact]
    public async Task CancelKeepsPartAndPausesItem()
    {
        var body = new Body(First, stallAfter: 150); var ranges = new List<string?>();
        using var http = Http(request =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            return ranges.Count == 1 ? Reply(new StreamContent(body)) : Reply(new ByteArrayContent(First[150..]) { Headers = { ContentRange = new ContentRangeHeaderValue(150, 299, 300) } }, HttpStatusCode.PartialContent);
        });
        await using var queue = Queue(http);
        var item = Add(queue, "o/a", FileA);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        queue.Pause(item);
        Assert.Equal(DownloadItemState.Paused, item.State);
        await Until(() => !queue.IsActive && File.Exists(Part("o/a", FileA)) && new FileInfo(Part("o/a", FileA)).Length == 150);
        await Task.Delay(50); // A cancelled transfer must not overwrite Paused afterwards.
        Assert.Equal(DownloadItemState.Paused, item.State);
        queue.Resume(item);
        await Until(() => item.State == DownloadItemState.Completed);
        Assert.Equal(new string[] { null!, "bytes=150-" }, ranges.ToArray());
        Assert.Equal(First, File.ReadAllBytes(Final("o/a", FileA)));
        Assert.False(File.Exists(Part("o/a", FileA)));
    }

    [Fact]
    public async Task FailureMarksItemFailedWithMessage()
    {
        using var http = Http(request => Reply(new ByteArrayContent(request.RequestUri!.AbsolutePath.Contains("/o/a/") ? First : Second)));
        await using var queue = Queue(http);
        var a = Add(queue, "o/a", FileA with { Sha256 = new string('0', 64) }); var b = Add(queue, "o/b", FileB);
        await Until(() => a.State == DownloadItemState.Failed && b.State == DownloadItemState.Completed);
        Assert.Equal(Corrupted, a.Message);
        Assert.Equal(Final("o/b", FileB), b.FinalPath);
        Assert.Null(a.FinalPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("relative")]
    public void EnqueueRequiresExistingModelsFolder(string folder)
    {
        using var http = Http(_ => throw new Exception("No request expected"));
        var queue = new DownloadQueue(() => folder == "missing" ? Path.Combine(_root, "missing") : folder, http);
        var ex = Assert.Throws<InvalidOperationException>(() => Add(queue, "o/a", FileA));
        Assert.Equal("Set a models folder in Settings first.", ex.Message);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task EnqueueRejectsDuplicates()
    {
        var body = new Body(First, stallAfter: 0);
        using var http = Http(_ => Reply(new StreamContent(body)));
        await using var queue = Queue(http);
        var a = Add(queue, "o/a", FileA); var b = Add(queue, "o/b", FileB);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(a, Add(queue, "O/A", FileA)); // Downloading
        Assert.Same(b, Add(queue, "o/b", FileB)); // Queued
        Assert.NotSame(a, Add(queue, "o/a", FileA, "Q8_0")); // Another quantization is a different download.
        Assert.Equal(3, queue.Items.Count);
    }

    [Fact]
    public async Task PausedDuplicateIsQueuedAgainAndQueuedItemCanBePaused()
    {
        var body = new Body(First, stallAfter: 0);
        using var http = Http(_ => Reply(new StreamContent(body)));
        await using var queue = Queue(http);
        var a = Add(queue, "o/a", FileA); var b = Add(queue, "o/b", FileB);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        queue.Pause(b);
        Assert.Equal(DownloadItemState.Paused, b.State);
        queue.Resume(a); // Only paused or failed items are affected.
        Assert.Equal(DownloadItemState.Downloading, a.State);
        Assert.Same(b, Add(queue, "o/b", FileB));
        Assert.Equal(DownloadItemState.Queued, b.State);
        Assert.Equal(2, queue.Items.Count);
    }

    [Fact]
    public async Task RemoveCancelsActiveItemAndDeletesPart()
    {
        var body = new Body(First, stallAfter: 150);
        using var http = Http(_ => Reply(new StreamContent(body)));
        await using var queue = Queue(http);
        var item = Add(queue, "o/a", FileA);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        queue.Remove(item);
        Assert.Empty(queue.Items);
        await Until(() => !queue.IsActive && !File.Exists(Part("o/a", FileA)));
        Assert.False(File.Exists(Final("o/a", FileA)));
    }

    [Fact]
    public async Task RemoveThenEnqueueAgainStartsFromScratchAndVerifies()
    {
        var body = new Body(First, stallAfter: 150); var ranges = new List<string?>();
        using var http = Http(request =>
        {
            lock (ranges) { ranges.Add(request.Headers.Range?.ToString()); return ranges.Count == 1 ? Reply(new StreamContent(body)) : Reply(new ByteArrayContent(First)); }
        });
        await using var queue = Queue(http);
        var x = Add(queue, "o/a", FileA);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        queue.Remove(x);
        var y = Add(queue, "o/a", FileA);
        Assert.NotSame(x, y);
        await Until(() => y.State == DownloadItemState.Completed);
        // The old .part is gone before the new transfer starts, so nothing can be resumed from or truncated under it.
        lock (ranges) Assert.Equal(new string?[] { null, null }, ranges.ToArray());
        Assert.Equal(First, File.ReadAllBytes(y.FinalPath!));
        Assert.Equal([y], queue.Items);
    }

    [Fact]
    public async Task ThrowingSubscriberDoesNotAffectQueue()
    {
        var body = new Body(First, stallAfter: 150);
        using var http = Http(request => Reply(request.RequestUri!.AbsolutePath.Contains("/o/a/") ? new StreamContent(body) : new ByteArrayContent(Second)));
        await using var queue = Queue(http); var seen = 0;
        queue.Changed += _ => throw new InvalidOperationException("Faulty subscriber");
        queue.Changed += _ => Interlocked.Increment(ref seen);
        var a = Add(queue, "o/a", FileA); var b = Add(queue, "o/b", FileB);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        queue.Pause(a);
        await Until(() => b.State == DownloadItemState.Completed);
        await queue.CancelAllAsync();
        Assert.False(queue.IsActive);
        Assert.Equal(DownloadItemState.Paused, a.State);
        Assert.True(seen > 0);
    }

    [Fact]
    public async Task EnqueueAfterDisposeThrowsAndPauseIgnoresRemovedItems()
    {
        var body = new Body(First, stallAfter: 0);
        using var http = Http(_ => Reply(new StreamContent(body)));
        var queue = Queue(http);
        var a = Add(queue, "o/a", FileA); var b = Add(queue, "o/b", FileB);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        queue.Remove(b); queue.Pause(b);
        Assert.Equal(DownloadItemState.Queued, b.State);
        await queue.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => Add(queue, "o/c", FileA));
        Assert.Equal(DownloadItemState.Paused, a.State);
    }

    [Fact]
    public async Task RemoveOfCompletedItemKeepsFile()
    {
        using var http = Http(_ => Reply(new ByteArrayContent(First)));
        await using var queue = Queue(http);
        var item = Add(queue, "o/a", FileA);
        await Until(() => item.State == DownloadItemState.Completed);
        queue.Remove(item);
        Assert.Empty(queue.Items);
        Assert.True(File.Exists(Final("o/a", FileA)));
    }

    [Fact]
    public async Task CancelAllAsyncWaitsForActiveDownload()
    {
        var body = new Body(First, stallAfter: 150);
        using var http = Http(_ => Reply(new StreamContent(body)));
        await using var queue = Queue(http);
        var a = Add(queue, "o/a", FileA); var b = Add(queue, "o/b", FileB);
        await body.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.CancelAllAsync();
        // The transfer has released its file: an exclusive open only works once the download let go of it.
        using (var exclusive = new FileStream(Part("o/a", FileA), FileMode.Open, FileAccess.Read, FileShare.None)) Assert.Equal(150, exclusive.Length);
        Assert.False(queue.IsActive);
        Assert.Equal([DownloadItemState.Paused, DownloadItemState.Paused], queue.Items.Select(i => i.State));
        Assert.False(File.Exists(Part("o/b", FileB)));
    }

    [Fact]
    public async Task GpuInfoPicksLargestDevice()
    {
        GpuStatistics Gpu(string name, double? total, double? used) => new("0", name, null, used, total, null);
        var (gpu, error) = await GpuInfo.ReadAsync(_ => Task.FromResult<(IReadOnlyList<GpuStatistics>, string?)>(([Gpu("Small", 8192, 100), Gpu("Big", 16303, 1024), Gpu("Unknown", null, 5)], null)), default);
        Assert.Null(error);
        Assert.Equal(new GpuMemory("Big", 16303L << 20, 1024L << 20), gpu);
        (gpu, error) = await GpuInfo.ReadAsync(_ => Task.FromResult<(IReadOnlyList<GpuStatistics>, string?)>(([], null)), default);
        Assert.Null(gpu); Assert.Equal("No GPU memory information is available.", error);
        (gpu, error) = await GpuInfo.ReadAsync(_ => Task.FromResult<(IReadOnlyList<GpuStatistics>, string?)>(([Gpu("NoTotal", null, 1)], "nvidia-smi failed")), default);
        Assert.Null(gpu); Assert.Equal("nvidia-smi failed", error);
        Assert.True(GpuInfo.TotalRamBytes() > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // Forward-only body without Content-Length. Reaching stallAfter signals Stalled and waits for Release (or cancellation) before the rest is served.
    private sealed class Body(byte[] data, int stallAfter = int.MaxValue) : Stream
    {
        private int _position;
        private bool _released;
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_released && _position >= stallAfter && _position < data.Length)
            {
                Stalled.TrySetResult();
                await Release.Task.WaitAsync(token);
                _released = true;
            }
            var count = Math.Min(buffer.Length, (_released ? data.Length : Math.Min(stallAfter, data.Length)) - _position);
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

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
