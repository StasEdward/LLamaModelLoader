using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class ChatTests
{
    private static readonly ChatTarget Target = new("http://127.0.0.1:8080", "Local model alias", 123, DateTimeOffset.UtcNow);
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Response(string text, HttpStatusCode code = HttpStatusCode.OK, string type = "text/event-stream") =>
        new(code) { Content = new StringContent(text, Encoding.UTF8, type) };
    private static async Task<List<ChatUpdate>> ReadAsync(ChatClient client, CancellationToken token = default)
    {
        var updates = new List<ChatUpdate>();
        await foreach (var update in client.StreamAsync(Target, [new("user", "Hello")], 16384, token)) updates.Add(update);
        return updates;
    }

    [Fact]
    public async Task StreamsReasoningContentAndUsageAndSendsHistoryWithAlias()
    {
        using var client = new ChatClient(new Handler(async (request, token) =>
        {
            Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
            Assert.Equal(Target.Model, root.GetProperty("model").GetString()); Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal(16384, root.GetProperty("max_tokens").GetInt32());
            Assert.False(root.TryGetProperty("temperature", out _)); Assert.False(root.TryGetProperty("tools", out _));
            Assert.Equal(new[] { "system", "user", "assistant", "user" }, root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
            return Response(": heartbeat\n\n" +
                Event(new { choices = new[] { new { delta = new { reasoning_content = "Think " } } } }) +
                Event(new { choices = new[] { new { delta = new { reasoning = "more" } } } }) +
                Event(new { choices = new[] { new { delta = new { content = "Hello \u03a9" }, finish_reason = "stop" } } }) +
                Event(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 12, completion_tokens = 5 } }) + "data: [DONE]\n\n");
        }));
        var updates = new List<ChatUpdate>();
        await foreach (var u in client.StreamAsync(Target, [new("system", "Be concise"), new("user", "One"), new("assistant", "Two"), new("user", "Three")], 16384)) updates.Add(u);
        Assert.Equal("Think more", string.Concat(updates.Select(u => u.Reasoning)));
        Assert.Equal("Hello \u03a9", string.Concat(updates.Select(u => u.Content)));
        Assert.Equal(12, updates.Last().PromptTokens); Assert.Equal(5, updates.Last().CompletionTokens);
    }

    [Fact]
    public async Task AcceptsMultilineSseDataAndLengthFinish()
    {
        using var client = new ChatClient(new Handler((_, _) => Task.FromResult(Response("data: {\n" +
            "data: \"choices\":[{\"delta\":{\"content\":\"Partial\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n"))));
        var updates = await ReadAsync(client);
        Assert.Equal("length", Assert.Single(updates).FinishReason);
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"Partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"Partial\"},\"finish_reason\":\"stop\"}]}\n\n")]
    public async Task RejectsIncompleteStreams(string text)
    {
        using var client = new ChatClient(new Handler((_, _) => Task.FromResult(Response(text))));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(client));
    }

    [Theory]
    [InlineData(400, "application/json", "{\"error\":{\"message\":\"Context is full\"}}", "Context is full")]
    [InlineData(200, "text/event-stream", "data: {\"error\":{\"message\":\"No slot\"}}\n\n", "No slot")]
    [InlineData(200, "application/json", "{}", "event stream")]
    public async Task ReportsHttpAndStreamErrors(int code, string type, string text, string expected)
    {
        using var client = new ChatClient(new Handler((_, _) => Task.FromResult(Response(text, (HttpStatusCode)code, type))));
        var error = await Record.ExceptionAsync(() => ReadAsync(client)); Assert.NotNull(error); Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task RejectsToolCallsAndMalformedData()
    {
        using var client = new ChatClient(new Handler((_, _) => Task.FromResult(Response(Event(new { choices = new[] { new { delta = new { tool_calls = new[] { new { id = "call" } } } } } })) )));
        Assert.Contains("tool call", (await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(client))).Message);
        using var malformed = new ChatClient(new Handler((_, _) => Task.FromResult(Response("data: broken\n\n"))));
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(malformed));
    }

    [Fact]
    public async Task CancellationInterruptsStalledStreamAndDisposesIt()
    {
        using var stream = new StalledStream();
        using var client = new ChatClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream"); return Task.FromResult(response);
        }));
        using var cancellation = new CancellationTokenSource(); var task = ReadAsync(client, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task RestrictsRequestsToLocalServerAndBoundsHistory()
    {
        using var client = new ChatClient(new Handler((_, _) => throw new Exception("Invalid request was sent")));
        async Task Read(ChatTarget target, ChatMessage[] messages)
        { await foreach (var _ in client.StreamAsync(target, messages, 10)) { } }
        await Assert.ThrowsAsync<ArgumentException>(() => Read(Target with { BaseUrl = "https://example.com" }, [new("user", "hi")]));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(Target, [new("tool", "hi")]));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(Target, [new("user", new string('a', 1024 * 1024))]));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
