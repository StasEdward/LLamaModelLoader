using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLamaModelLoader.Core;
using Microsoft.VisualBasic.FileIO;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Read-only telemetry. Missing measurements remain unknown, never fabricated as zero.</summary>
public sealed partial class StatisticsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<(IReadOnlyList<GpuStatistics> Gpus, string? Error)>> _readGpus;
    public StatisticsClient(HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<(IReadOnlyList<GpuStatistics> Gpus, string? Error)>>? readGpus = null)
    {
        _readGpus = readGpus ?? ReadGpusAsync;
        _http = new(handler ?? new HttpClientHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    }

    public async Task<StatisticsSnapshot> ReadAsync(string? baseUrl, CancellationToken token)
    {
        var metricsTask = GetAsync(baseUrl, "/metrics", token);
        var slotsTask = GetAsync(baseUrl, "/slots", token);
        var gpuTask = _readGpus(token);
        await Task.WhenAll(metricsTask, slotsTask, gpuTask);
        var metrics = await metricsTask; var slots = await slotsTask; var gpu = await gpuTask;
        IReadOnlyList<SlotStatistics> parsedSlots = [];
        if (slots.Body is { } body)
        {
            try { parsedSlots = ParseSlots(body); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
            { slots = (null, "Slots: unrecognized response format."); }
        }
        var parsedMetrics = ParseMetrics(metrics.Body ?? "");
        var metricsError = metrics.Error ?? (parsedMetrics.Count == 0 ? "The server did not return recognizable metrics." : null);
        return new(parsedMetrics, parsedSlots, gpu.Gpus, metricsError, slots.Error, gpu.Error);
    }

    private async Task<(string? Body, string? Error)> GetAsync(string? url, string endpoint, CancellationToken token)
    {
        if (url is null) return (null, "The server is stopped or still loading.");
        try
        {
            using var response = await _http.GetAsync(url + endpoint, token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
                return (null, endpoint == "/metrics" ? "Metrics are disabled or unsupported. The next server start will enable --metrics if this build supports it." : "Slot information is disabled or unsupported.");
            if (!response.IsSuccessStatusCode) return (null, $"{endpoint}: HTTP {(int)response.StatusCode}.");
            return (await response.Content.ReadAsStringAsync(token), null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (null, endpoint + ": request timed out."); }
        catch (HttpRequestException) { return (null, endpoint + ": API is unavailable."); }
    }

    [GeneratedRegex(@"^(llamacpp:[a-zA-Z0-9_]+)(?:\{(?:[^""\\}]|\\.|""(?:[^""\\]|\\.)*"")*\})?\s+(\S+)")]
    private static partial Regex MetricPattern();

    public static IReadOnlyDictionary<string, double> ParseMetrics(string text)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = MetricPattern().Match(line.Trim());
            if (match.Success && Number(match.Groups[2].Value) is { } value)
                values[match.Groups[1].Value] = value;
        }
        return values;
    }

    public static IReadOnlyList<SlotStatistics> ParseSlots(string text)
    {
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
        var result = new List<SlotStatistics>();
        foreach (var slot in json.RootElement.EnumerateArray())
        {
            var id = Integer(slot, "id");
            if (id is null) continue;
            long? decoded = Integer(slot, "n_decoded");
            if (slot.TryGetProperty("next_token", out var next))
            {
                if (next.ValueKind == JsonValueKind.Object) decoded ??= Integer(next, "n_decoded");
                else if (next.ValueKind == JsonValueKind.Array)
                    foreach (var item in next.EnumerateArray()) decoded ??= Integer(item, "n_decoded");
            }
            result.Add(new(checked((int)id), slot.TryGetProperty("is_processing", out var active) && active.ValueKind == JsonValueKind.True,
                Integer(slot, "n_ctx"), Integer(slot, "n_prompt_tokens"), decoded));
        }
        return result;
    }
    private static long? Integer(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : null;

    public static IReadOnlyList<GpuStatistics> ParseGpus(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv)) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var result = new List<GpuStatistics>();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields();
            if (row is not { Length: 6 }) continue;
            result.Add(new(row[0].Trim(), row[1].Trim(), Number(row[2]), Number(row[3]), Number(row[4]), Number(row[5])));
        }
        return result;
    }

    public static async Task<(IReadOnlyList<GpuStatistics> Gpus, string? Error)> ReadGpusAsync(CancellationToken token)
    {
        var path = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe") }.FirstOrDefault(File.Exists);
        if (path is null) return ([], "NVIDIA: nvidia-smi was not found. Telemetry for other GPUs is not yet supported.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            var text = await output;
            if (process.ExitCode != 0) return ([], "NVIDIA: the driver did not return statistics.");
            var gpus = ParseGpus(text);
            return (gpus, gpus.Count == 0 ? "NVIDIA: no devices found." : null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ([], "NVIDIA: request timed out."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or MalformedLineException)
        { return ([], "NVIDIA: statistics are unavailable."); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
        }
    }
    public void Dispose() => _http.Dispose();
}
