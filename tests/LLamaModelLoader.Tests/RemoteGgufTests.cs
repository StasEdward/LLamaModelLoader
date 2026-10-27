using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class RemoteGgufTests
{
    private static readonly string Sha = new('a', 40);

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        public int Entries { get; private set; }
        public byte[] Bytes => _stream.ToArray();
        private void U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _stream.Write(b); }
        private void U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); _stream.Write(b); }
        private void Text(string value) { var b = Encoding.UTF8.GetBytes(value); U64((ulong)b.Length); _stream.Write(b); }
        public void StringValue(string key, string value) { Text(key); U32(8); Text(value); Entries++; }
        public void Integer(string key, uint value) { Text(key); U32(4); U32(value); Entries++; }
        public void StringArray(string key, params string[] values) { Text(key); U32(9); U32(8); U64((ulong)values.Length); foreach (var value in values) Text(value); Entries++; }
        public void DeclaredString(string key, ulong length) { Text(key); U32(8); U64(length); Entries++; } // The payload is not written.
        public void Header(ulong tensors, ulong entries) { _stream.Write("GGUF"u8); U32(3); U64(tensors); U64(entries); }
    }

    private static byte[] Header(Action<Writer> extra)
    {
        var body = new Writer();
        body.StringValue("general.architecture", "llama"); body.Integer("llama.block_count", 4);
        body.Integer("llama.attention.head_count", 32); body.Integer("llama.embedding_length", 4096);
        body.StringArray("tokenizer.ggml.tokens", "a", "b", "c");
        extra(body);
        var output = new Writer();
        output.Header(0, (ulong)body.Entries);
        return [.. output.Bytes, .. body.Bytes];
    }

    // About 3.2 MB of strings, so the header needs the third (4 MiB) request.
    private static byte[] LargeHeader() => Header(w => w.StringArray("tokenizer.ggml.merges", [.. Enumerable.Repeat(new string('x', 1000), 3200)]));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    // Honours a single "bytes=a-b" range like huggingface.co: the end is clamped to the file and the reply is 206 with Content-Range.
    private static HttpClient Server(long total, Func<long, int, byte[]> read, List<(long From, long To)> ranges, Func<HttpRequestMessage, HttpResponseMessage?>? special = null) => new(new Handler((request, _) =>
    {
        if (special?.Invoke(request) is { } custom) return Task.FromResult(custom);
        var range = request.Headers.Range!.Ranges.Single();
        var from = range.From!.Value; var to = Math.Min(range.To!.Value, total - 1);
        lock (ranges) ranges.Add((from, range.To!.Value));
        var content = new ByteArrayContent(read(from, (int)(to - from + 1)));
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
    }));

    private static HttpClient Serve(byte[] file, List<(long From, long To)> ranges) => Server(file.Length, (from, count) => file.AsSpan((int)from, count).ToArray(), ranges);

    [Fact]
    public async Task FetchesGrowingPrefixUntilHeaderParses()
    {
        var file = LargeHeader(); var ranges = new List<(long, long)>();
        Assert.InRange(file.Length, 2 * 1024 * 1024 + 1, 4 * 1024 * 1024);
        var shape = await RemoteGguf.ReadKvShapeAsync(Serve(file, ranges), "o/r", Sha, "model.gguf", CancellationToken.None);
        Assert.Equal("llama", shape.Architecture); Assert.Equal(4, shape.Layers); Assert.Equal(3UL, shape.VocabularySize);
        // The third request would end at 4194303 but is clamped to the known file length, never beyond it.
        Assert.Equal([(0L, 1048575L), (1048576L, 2097151L), (2097152L, file.Length - 1L)], ranges);
    }

    [Fact]
    public async Task StopsAtFileEnd()
    {
        var file = Header(_ => { }); var padded = new byte[200 * 1024]; file.CopyTo(padded, 0); var ranges = new List<(long, long)>();
        var shape = await RemoteGguf.ReadKvShapeAsync(Serve(padded, ranges), "o/r", Sha, "model.gguf", CancellationToken.None);
        Assert.Equal(4, shape.Layers);
        Assert.Equal([(0L, 1048575L)], ranges);
    }

    [Fact]
    public async Task ReportsTruncatedHeaderWhenFileEndsFirst()
    {
        var file = LargeHeader()[..(2 * 1024 * 1024)];
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGguf.ReadKvShapeAsync(Serve(file, []), "o/r", Sha, "model.gguf", CancellationToken.None));
        Assert.Contains("truncated", error.Message);
    }

    [Fact]
    public async Task RejectsServersWithoutRangeSupport()
    {
        var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Header(_ => { })) })));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGguf.ReadKvShapeAsync(http, "o/r", Sha, "model.gguf", CancellationToken.None));
        Assert.Contains("range", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "The file or repository was not found or is not public.")]
    [InlineData(HttpStatusCode.Unauthorized, "The file or repository was not found or is not public.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Hugging Face refused the request (rate limit). Try again later.")]
    [InlineData(HttpStatusCode.Forbidden, "Hugging Face refused the request (rate limit). Try again later.")]
    public async Task ReportsRefusedRequests(HttpStatusCode status, string message)
    {
        var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RemoteGguf.ReadKvShapeAsync(http, "o/r", Sha, "model.gguf", CancellationToken.None));
        Assert.Equal(message, error.Message);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task RejectsRangesThatDoNotStartWhereRequestedAndOversizedBodies()
    {
        var file = LargeHeader();
        var wrongStart = Server(file.Length, (from, count) => file.AsSpan((int)from, count).ToArray(), [], request =>
        {
            if (request.Headers.Range!.Ranges.Single().From == 0) return null;
            var content = new ByteArrayContent(new byte[16]); content.Headers.ContentRange = new ContentRangeHeaderValue(0, 15, file.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        });
        Assert.Contains("range", (await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGguf.ReadKvShapeAsync(wrongStart, "o/r", Sha, "model.gguf", CancellationToken.None))).Message);
        var oversized = new HttpClient(new Handler((_, _) =>
        {
            var content = new ByteArrayContent(new byte[2048]); content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1023, 1 << 20);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGguf.ReadKvShapeAsync(oversized, "o/r", Sha, "model.gguf", CancellationToken.None));
    }

    [Fact]
    public async Task EnforcesPrefixLimit()
    {
        // A general.name value that claims 300 MiB: every prefix is truncated, and the server has more bytes than the limit allows.
        var head = Header(w => w.DeclaredString("general.name", 300UL << 20));
        const long total = 512L << 20; var ranges = new List<(long From, long To)>();
        var http = Server(total, (from, count) =>
        {
            var bytes = new byte[count]; Array.Fill(bytes, (byte)'a');
            if (from < head.Length) head.AsSpan((int)from, (int)Math.Min(head.Length - from, count)).CopyTo(bytes);
            return bytes;
        }, ranges);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGguf.ReadKvShapeAsync(http, "o/r", Sha, "model.gguf", CancellationToken.None));
        Assert.Contains("64 MiB", error.Message);
        Assert.All(ranges, r => Assert.True(r.From < RemoteGguf.PrefixLimit && r.To < RemoteGguf.PrefixLimit));
        Assert.Equal(RemoteGguf.PrefixLimit - 1, ranges[^1].To);
    }

    [Fact]
    public async Task PropagatesMalformedHeaders()
    {
        var http = Serve(new byte[4096], []);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGguf.ReadKvShapeAsync(http, "o/r", Sha, "model.gguf", CancellationToken.None));
    }

    [Fact]
    public async Task SendsRangeRequestWithoutCredentials()
    {
        HttpRequestMessage? seen = null; var file = Header(_ => { });
        var http = Server(file.Length, (from, count) => file.AsSpan((int)from, count).ToArray(), [], request => { seen = request; return null; });
        await RemoteGguf.ReadKvShapeAsync(http, "o/r", Sha, "Q4 K/model.gguf", CancellationToken.None);
        Assert.Equal($"https://huggingface.co/o/r/resolve/{Sha}/Q4%20K/model.gguf", seen!.RequestUri!.AbsoluteUri);
        Assert.Equal("LLamaModelLoader/1.0", seen.Headers.UserAgent.ToString());
        Assert.Null(seen.Headers.Authorization);
    }

    [Fact]
    public void FileUriEscapesSegments()
    {
        Assert.Equal($"https://huggingface.co/o/r/resolve/{Sha}/Q4%20K/M%231.gguf", RemoteGguf.FileUri("o/r", Sha, "Q4 K/M#1.gguf").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => RemoteGguf.FileUri("o/r", Sha, "../x.gguf"));
        Assert.Throws<ArgumentException>(() => RemoteGguf.FileUri("o/r", Sha, "a:b.gguf"));
        Assert.Throws<ArgumentException>(() => RemoteGguf.FileUri("o/r", Sha, "weights.bin"));
        Assert.Throws<ArgumentException>(() => RemoteGguf.FileUri("bad", Sha, "m.gguf"));
        Assert.Throws<ArgumentException>(() => RemoteGguf.FileUri("o/r", "abc", "m.gguf"));
        Assert.Throws<ArgumentException>(() => RemoteGguf.FileUri("o/r", "main", "m.gguf"));
    }
}
