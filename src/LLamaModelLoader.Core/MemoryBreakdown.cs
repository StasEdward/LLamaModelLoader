namespace LLamaModelLoader.Core;

public enum MemoryCategory { Weights, MainKvCache, RecurrentState, MtpKvCache, Compute, Output }

public sealed record MemoryAmount(double? GpuMiB = null, double? HostMiB = null);

public sealed record MemoryBreakdown(
    MemoryAmount Weights, MemoryAmount MainKvCache, MemoryAmount RecurrentState,
    MemoryAmount MtpKvCache, MemoryAmount Compute, MemoryAmount Output)
{
    public static MemoryBreakdown Empty { get; } = new(new(), new(), new(), new(), new(), new());
    public bool HasData => All.Any(a => a.GpuMiB.HasValue || a.HostMiB.HasValue);
    private MemoryAmount[] All => [Weights, MainKvCache, RecurrentState, MtpKvCache, Compute, Output];
    public MemoryAmount Total => new(Sum(All.Select(a => a.GpuMiB)), Sum(All.Select(a => a.HostMiB)));
    private static double? Sum(IEnumerable<double?> values)
    {
        var reported = values.Where(v => v.HasValue).ToArray();
        return reported.Length == 0 ? null : reported.Sum(v => v!.Value);
    }
}
