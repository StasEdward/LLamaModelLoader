using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Contains("--version")) { Console.WriteLine("Fake llama-server test fixture 1.0"); return; }
if (args.Contains("--help"))
{
    Console.WriteLine("--test-completion-delay --test-fail-batch --test-memory --spec-type --spec-draft-n-max --spec-draft-p-min --log-verbosity --verbosity -lv --verbose --log-disable --alias --metrics --model --host --port --parallel --ctx-size --gpu-layers --threads --threads-batch --batch-size --ubatch-size --flash-attn --cache-type-k --cache-type-v --load-mode --fit --temp --top-k --top-p --min-p --repeat-penalty --presence-penalty --frequency-penalty --seed --predict --jinja --no-jinja --chat-template --reasoning --reasoning-budget --reasoning-budget-message --test-delay --test-crash --test-stderr --test-no-health --test-args-file --test-health-flap");
    return;
}
string? Value(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (Value("--test-args-file") is { } output) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { args, env = Environment.GetEnvironmentVariable("LLAMA_ARG_CTX_SIZE") }));
if (args.Contains("--test-memory"))
{
    Console.Error.WriteLine("load_tensors: CUDA0 model buffer size = 11107.00 MiB");
    Console.Error.WriteLine("load_tensors: CPU_Mapped model buffer size = 128.00 MiB");
    Console.Error.WriteLine("llama_context: constructing llama_context");
    Console.Error.WriteLine("llama_kv_cache: CUDA0 KV buffer size = 1088.00 MiB");
    Console.Error.WriteLine("llama_memory_recurrent: CUDA0 RS buffer size = 599.00 MiB");
    Console.Error.WriteLine("graph_reserve: CUDA0 compute buffer size = 240.00 MiB");
    Console.Error.WriteLine("graph_reserve: CPU compute buffer size = 8.00 MiB");
    Console.Error.WriteLine("common_speculative_init_result: creating MTP draft context against the target model");
    Console.Error.WriteLine("llama_context: constructing llama_context");
    Console.Error.WriteLine("llama_kv_cache: CUDA0 KV buffer size = 128.00 MiB");
    Console.Error.WriteLine("graph_reserve: CUDA0 compute buffer size = 130.00 MiB");
    Console.Error.WriteLine("llama_context: CUDA_Host output buffer size = 1.00 MiB");
    Console.Error.WriteLine("srv llama_server: model loaded");
}
if (args.Contains("--test-stderr")) for (var i = 0; i < 3000; i++) Console.Error.WriteLine(new string('x', 100));
if (args.Contains("--test-crash")) { Console.Error.WriteLine("simulated load failure"); Environment.Exit(42); }
if (args.Contains("--test-fail-batch") && Value("--batch-size") == "2048") Environment.Exit(43);
var delay = int.Parse(Value("--test-delay") ?? "100");
var started = DateTime.UtcNow;
var server = new TcpListener(IPAddress.Loopback, int.Parse(Value("--port")!));
server.Start();
Console.WriteLine("fixture started");
while (true)
{
    var client = await server.AcceptTcpClientAsync();
    _ = Handle(client);
}
async Task Handle(TcpClient client)
{
    try
    {
    using var ownedClient = client;
    await using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    var request = await reader.ReadLineAsync() ?? "";
    var length = 0;
    while (await reader.ReadLineAsync() is { Length: > 0 } header)
        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim());
    var requestBody = new char[length]; var read = 0;
    while (read < length) { var n = await reader.ReadAsync(requestBody.AsMemory(read)); if (n == 0) break; read += n; }
    if (request.StartsWith("POST /v1/chat/completions "))
    {
        using var json = JsonDocument.Parse(new string(requestBody));
        var root = json.RootElement;
        if (root.GetProperty("model").GetString() != Value("--alias") || !root.GetProperty("stream").GetBoolean())
            throw new InvalidDataException("Chat did not use the running alias and streaming mode");
        var messages = root.GetProperty("messages");
        var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        var messageCount = messages.GetArrayLength();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
        async Task Event(object value) => await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(value) + "\n\n"));
        await Event(new { choices = new[] { new { delta = new { reasoning_content = "Checking the test message." }, finish_reason = (string?)null } } });
        await Task.Delay(200);
        await Event(new { choices = new[] { new { delta = new { content = "Hello from the local model. " }, finish_reason = (string?)null } } });
        if (prompt == "[stall]") await Task.Delay(3000);
        await Task.Delay(150);
        await Event(new { choices = new[] { new { delta = new { content = $"Received {messageCount} message(s)." }, finish_reason = (string?)null } } });
        await Event(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 30, completion_tokens = 16 } });
        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
        return;
    }
    if (request.StartsWith("POST /completion "))
    {
        using var json = JsonDocument.Parse(new string(requestBody));
        var count = json.RootElement.GetProperty("n_predict").GetInt32();
        if (json.RootElement.GetProperty("cache_prompt").GetBoolean() || !json.RootElement.GetProperty("ignore_eos").GetBoolean())
            throw new InvalidDataException("Invalid benchmark request");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
        await Task.Delay(int.Parse(Value("--test-completion-delay") ?? "10"));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"content\":\"test\",\"stop\":false}\n\n"));
        var speed = Value("--batch-size") == "512" ? 80d : 40d;
        var end = JsonSerializer.Serialize(new { content = "", stop = true, truncated = false,
            timings = new { prompt_n = 64, prompt_ms = 100, predicted_n = count, predicted_ms = count * 1000 / speed, cache_n = 0 } });
        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + end + "\n\n"));
        return;
    }
    var loading = (DateTime.UtcNow - started).TotalMilliseconds < delay || args.Contains("--test-no-health");
    var props = request.StartsWith("GET /props ");
    var body = props ? JsonSerializer.Serialize(new { model_path = Value("--model") }) : loading ? "{\"error\":\"loading\"}" : "{\"status\":\"ok\"}";
    if (request.StartsWith("GET /v1/models ")) body = JsonSerializer.Serialize(new { data = new[] { new { id = Value("--alias") ?? Value("--model") } } });
    if (request.StartsWith("GET /metrics ") && args.Contains("--metrics")) body = "# TYPE llamacpp:tokens_predicted_total counter\nllamacpp:tokens_predicted_total 42\nllamacpp:predicted_tokens_seconds 12.5\nllamacpp:requests_processing 1\n";
    if (request.StartsWith("GET /slots ")) body = JsonSerializer.Serialize(new[] { new { id = 0, n_ctx = int.Parse(Value("--ctx-size") ?? "4096"), is_processing = true, n_prompt_tokens = 123, next_token = new { n_decoded = 42 } } });
    var bytes = Encoding.UTF8.GetBytes(body);
    var headers = $"HTTP/1.1 {(loading && !props ? "503 Service Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
    await stream.WriteAsync(bytes);
    }
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
}
