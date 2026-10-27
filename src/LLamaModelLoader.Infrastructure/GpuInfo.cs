using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed record GpuMemory(string Name, long TotalBytes, long UsedBytes);

/// <summary>Memory of the largest GPU, the one a model is most likely to be offloaded to.</summary>
public static class GpuInfo
{
    public static async Task<(GpuMemory? Gpu, string? Error)> ReadAsync(Func<CancellationToken, Task<(IReadOnlyList<GpuStatistics> Gpus, string? Error)>>? read, CancellationToken token)
    {
        var (gpus, error) = await (read ?? StatisticsClient.ReadGpusAsync)(token);
        var largest = gpus.Where(g => g.TotalMiB is not null).MaxBy(g => g.TotalMiB);
        return largest is null
            ? (null, error ?? "No GPU memory information is available.")
            : (new GpuMemory(largest.Name, MiB(largest.TotalMiB), MiB(largest.UsedMiB)), null);
    }

    public static long TotalRamBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    private static long MiB(double? value) => (long)((value ?? 0) * 1048576);
}
