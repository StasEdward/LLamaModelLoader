using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Contains("--version")) { Console.WriteLine("Fake llama-server test fixture 1.0"); return; }
if (args.Contains("--help"))
{
    Console.WriteLine("--test-memory --log-verbosity --verbosity -lv --verbose --log-disable --alias --metrics --model --host --port --parallel --ctx-size --gpu-layers --threads --threads-batch --batch-size --ubatch-size --flash-attn --cache-type-k --cache-type-v --load-mode --fit --temp --top-k --top-p --min-p --repeat-penalty --presence-penalty --frequency-penalty --seed --predict --jinja --no-jinja --chat-template --reasoning --test-delay --test-crash --test-stderr --test-no-health --test-args-file --test-health-flap");
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
var delay = int.Parse(Value("--test-delay") ?? "100");
var started = DateTime.UtcNow;
var server = new TcpListener(IPAddress.Loopback, int.Parse(Value("--port")!));
server.Start();
Console.WriteLine("fixture started");
while (true)
{
    using var client = await server.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    var request = await reader.ReadLineAsync() ?? "";
    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
    var loading = (DateTime.UtcNow - started).TotalMilliseconds < delay || args.Contains("--test-no-health");
    var props = request.StartsWith("GET /props ");
    var body = props ? JsonSerializer.Serialize(new { model_path = Value("--model") }) : loading ? "{\"error\":\"loading\"}" : "{\"status\":\"ok\"}";
    if (request.StartsWith("GET /v1/models ")) body = JsonSerializer.Serialize(new { data = new[] { new { id = Value("--alias") ?? Value("--model") } } });
    if (request.StartsWith("GET /metrics ") && args.Contains("--metrics")) body = "# TYPE llamacpp:tokens_predicted_total counter\nllamacpp:tokens_predicted_total 42\nllamacpp:predicted_tokens_seconds 12.5\nllamacpp:requests_processing 1\n";
    if (request.StartsWith("GET /slots ")) body = "[{\"id\":0,\"n_ctx\":4096,\"is_processing\":true,\"n_prompt_tokens\":123,\"next_token\":{\"n_decoded\":42}}]";
    var bytes = Encoding.UTF8.GetBytes(body);
    var headers = $"HTTP/1.1 {(loading && !props ? "503 Service Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
    await stream.WriteAsync(bytes);
}
