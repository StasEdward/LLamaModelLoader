namespace LLamaModelLoader.Core;

public sealed record ProcessResources(TimeSpan CpuTime, long WorkingSetBytes, int ProcessCount);
public sealed record SlotStatistics(int Id, bool Processing, long? Context, long? PromptTokens, long? GeneratedTokens);
public sealed record GpuStatistics(string Index, string Name, double? Utilization, double? UsedMiB, double? TotalMiB, double? Temperature);
public sealed record StatisticsSnapshot(
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<SlotStatistics> Slots,
    IReadOnlyList<GpuStatistics> Gpus,
    string? MetricsError = null, string? SlotsError = null, string? GpuError = null)
{
    public double? Metric(string name) => Metrics.TryGetValue("llamacpp:" + name, out var value) ? value : null;

    // Use lifetime counters, not sampled rates: idle polls and reopening the window
    // must not change the average. A new server process starts new counters.
    public double? SessionGenerationTokensPerSecond
    {
        get
        {
            if (Metric("tokens_predicted_total") is not { } tokens ||
                Metric("tokens_predicted_seconds_total") is not { } seconds ||
                !double.IsFinite(tokens) || !double.IsFinite(seconds) || tokens < 0 || seconds <= 0) return null;
            var rate = tokens / seconds;
            return double.IsFinite(rate) ? rate : null;
        }
    }
}
