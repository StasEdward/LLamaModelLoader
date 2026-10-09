using System.Globalization;
using System.Text.Json;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class MemoryTests
{
    private static MemoryLogParser Parse(params string[] lines)
    {
        var parser = new MemoryLogParser();
        foreach (var line in lines) parser.Add(line);
        return parser;
    }

    [Fact]
    public void HybridMtpSeparatesCachesAndSumsBothContextsWithoutSummaryDoubleCounting()
    {
        var parser = Parse(
            "load_tensors: CUDA0 model buffer size = 11107.00 MiB",
            "llama_context: constructing llama_context",
            "llama_kv_cache: CUDA0 KV buffer size = 1088.00 MiB",
            "llama_kv_cache: size = 1088.00 MiB, K (q8_0): 544.00 MiB, V (q8_0): 544.00 MiB",
            "llama_memory_recurrent: CUDA0 RS buffer size = 599.00 MiB",
            "llama_memory_recurrent: size = 599.00 MiB, R (f32): 40.00 MiB, S (f32): 559.00 MiB",
            "graph_reserve: CUDA0 compute buffer size = 240.00 MiB",
            "common_speculative_init_result: creating MTP draft context against the target model 'model.gguf'",
            "llama_context: constructing llama_context",
            "llama_kv_cache: CUDA0 KV buffer size = 128.00 MiB",
            "graph_reserve: CUDA0 compute buffer size = 130.00 MiB",
            "graph_reserve: CUDA0 compute buffer size = 130.00 MiB");
        var result = parser.Snapshot;
        Assert.Equal(11107, result.Weights.GpuMiB);
        Assert.Equal(1088, result.MainKvCache.GpuMiB);
        Assert.Equal(599, result.RecurrentState.GpuMiB);
        Assert.Equal(128, result.MtpKvCache.GpuMiB);
        Assert.Equal(370, result.Compute.GpuMiB);
        Assert.Equal(13292, result.Total.GpuMiB);
        Assert.Null(result.Total.HostMiB);
    }

    [Fact]
    public void MultipleGpusShardsAndHostMappingsAreSeparated()
    {
        var result = Parse(
            "llm_load_tensors: CUDA0 model buffer size = 1000.00 MiB",
            "llm_load_tensors: CUDA0 model buffer size = 500.00 MiB",
            "load_tensors: CUDA1 model buffer size = 2000.00 MiB",
            "load_tensors: CPU_Mapped model buffer size = 8000.00 MiB",
            "llama_context: CUDA_Host output buffer size = 1.00 MiB",
            "graph_reserve: CPU compute buffer size = 10.00 MiB").Snapshot;
        Assert.Equal(3500, result.Weights.GpuMiB);
        Assert.Equal(8000, result.Weights.HostMiB);
        Assert.Equal(8011, result.Total.HostMiB);
        Assert.Equal(3500, result.Total.GpuMiB);
    }

    [Fact]
    public void FitEstimatesAndPreviousAttemptsAreDiscarded()
    {
        var result = Parse(
            "print_info: no_alloc = 1",
            "load_tensors: loading model tensors (mmap = false)",
            "load_tensors: CUDA1 model buffer size = 0.00 MiB",
            "llama_kv_cache: CUDA1 KV buffer size = 2048.00 MiB",
            "print_info: no_alloc = 0",
            "load_tensors: loading model tensors (mmap = true)",
            "load_tensors: CUDA0 model buffer size = 9000.00 MiB",
            "llama_kv_cache: CUDA0 KV buffer size = 512.00 MiB",
            "load_tensors: loading model tensors (mmap = true)",
            "load_tensors: CPU_Mapped model buffer size = 9000.00 MiB").Snapshot;
        Assert.Null(result.Total.GpuMiB);
        Assert.Null(result.MainKvCache.GpuMiB);
        Assert.Equal(9000, result.Total.HostMiB);
    }

    [Fact]
    public void UnknownMissingAndMalformedValuesAreNotInvented()
    {
        var result = Parse("llama_kv_cache: size = 100.0 MiB",
            "load_tensors: CUDA0 model buffer size = NaN MiB",
            "graph_reserve: CUDA0 compute buffer size = -10 MiB",
            "graph_reserve: unknown compute buffer size = 100 MiB",
            "llama_context: compute buffer size of CUDA0 is 100 MiB").Snapshot;
        Assert.False(result.HasData);
        Assert.Null(result.Total.GpuMiB);
        Assert.Null(result.Total.HostMiB);
    }

    [Fact]
    public void ColorsJsonAndUnitsUseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var result = Parse("0.00.091.439 I \u001b[32mload_tensors: CUDA0 model buffer size = 1.25 GiB\u001b[0m",
                JsonSerializer.Serialize(new { type = "log", msg = "llama_kv_cache: CUDA0 KV buffer size = 512.00 KiB\n" }),
                "{bad json}").Snapshot;
            Assert.Equal(1280, result.Weights.GpuMiB);
            Assert.Equal(0.5, result.MainKvCache.GpuMiB);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void LoadingSnapshotIsStableAfterReadyAndIndependentBetweenSessions()
    {
        var parser = Parse("load_tensors: CUDA0 model buffer size = 42.00 MiB",
            "srv llama_server: model loaded");
        var loaded = parser.Snapshot;
        parser.Add("graph_reserve: CUDA0 compute buffer size = 500.00 MiB");
        Assert.Equal(loaded, parser.Snapshot);
        Assert.False(new MemoryLogParser().Snapshot.HasData);
    }

    [Fact]
    public void MultipleAttentionCachesAndRecurrentContextsAreSummed()
    {
        var result = Parse("llama_kv_cache: Vulkan0 KV buffer size = 100 MiB",
            "llama_kv_cache: Vulkan0 KV buffer size = 20 MiB",
            "llama_memory_recurrent: Vulkan0 RS buffer size = 50 MiB",
            "common_speculative_init_result: creating MTP draft context",
            "llama_memory_recurrent: Vulkan0 RS buffer size = 10 MiB").Snapshot;
        Assert.Equal(120, result.MainKvCache.GpuMiB);
        Assert.Equal(60, result.RecurrentState.GpuMiB);
        Assert.Null(result.MtpKvCache.GpuMiB);
    }
}
