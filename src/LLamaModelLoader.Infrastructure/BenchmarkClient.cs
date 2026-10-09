using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed class BenchmarkClient(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { UseProxy = false })
    { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    public async Task VerifyContextAsync(string url, int required, CancellationToken token)
    {
        using var response = await _http.GetAsync(url + "/slots", token);
        response.EnsureSuccessStatusCode();
        var slots = StatisticsClient.ParseSlots(await response.Content.ReadAsStringAsync(token));
        if (slots.Count != 1 || slots[0].Context is not { } context || context < required)
            throw new InvalidDataException("Cannot verify the requested context and one-slot configuration through /slots.");
    }

    public async Task<BenchmarkSample> MeasureAsync(string url, OptimizationOptions options, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/completion")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { prompt = options.Prompt, n_predict = options.GeneratedTokens,
                stream = true, cache_prompt = false, temperature = 0, seed = 42, ignore_eos = true,
                id_slot = 0, n_keep = 0 }), Encoding.UTF8, "application/json")
        };
        var timer = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        double? firstToken = null;
        var bytes = 0;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            bytes = checked(bytes + line.Length);
            if (bytes > 4 * 1024 * 1024) throw new InvalidDataException("Benchmark response exceeds 4 MiB.");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var text = line[5..].Trim();
            if (text == "[DONE]") break;
            using var json = JsonDocument.Parse(text); var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw new InvalidDataException("The server reported a benchmark error.");
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString()?.Length > 0)
                firstToken ??= timer.Elapsed.TotalMilliseconds;
            if (!root.TryGetProperty("stop", out var stop) || stop.ValueKind != JsonValueKind.True) continue;
            if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
                throw new InvalidDataException("The benchmark prompt was truncated; increase context or shorten the prompt.");
            if (firstToken is null || !root.TryGetProperty("timings", out var timings))
                throw new InvalidDataException("The server did not return streamed text and timing counters.");
            double Read(string key) => timings.TryGetProperty(key, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
                ? number : throw new InvalidDataException("Missing benchmark timing: " + key);
            var prompt = Read("prompt_n"); var predicted = Read("predicted_n");
            var promptMs = Read("prompt_ms"); var predictedMs = Read("predicted_ms");
            if (prompt <= 0 || Math.Truncate(prompt) != prompt || prompt > options.ContextSize || predicted != options.GeneratedTokens || promptMs <= 0 || predictedMs <= 0 ||
                (timings.TryGetProperty("cache_n", out var cache) && cache.GetDouble() > 0))
                throw new InvalidDataException("Incomplete generation, cached prompt, or invalid timing counters; result excluded.");
            if (prompt + predicted > options.ContextSize) throw new InvalidDataException("The workload exceeds the requested context.");
            var promptSpeed = prompt * 1000 / promptMs; var generationSpeed = predicted * 1000 / predictedMs;
            if (!double.IsFinite(promptSpeed) || !double.IsFinite(generationSpeed)) throw new InvalidDataException("Invalid benchmark rates.");
            return new(promptSpeed, generationSpeed, firstToken.Value, (int)prompt, (int)predicted);
        }
        throw new InvalidDataException("The benchmark stream ended without a completed result.");
    }
    public void Dispose() => _http.Dispose();
}
