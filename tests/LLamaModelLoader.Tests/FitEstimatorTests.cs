using LLamaModelLoader.Core;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class FitEstimatorTests
{
    private static readonly GgufKvShape Qwen = new("qwen3", 36, Enumerable.Repeat(8, 36).ToArray(), 128, 128, 151936, null, null, false, false);

    [Fact] public void KvCacheForQwen3At16kF16() => Assert.Equal(2_415_919_104L, FitEstimator.KvBytes(Qwen, 16384, "f16"));
    [Fact] public void KvCacheQ8() => Assert.Equal(1_283_457_024L, FitEstimator.KvBytes(Qwen, 16384, "q8_0"));
    [Fact] public void KvTypeIsCaseInsensitive() => Assert.Equal(FitEstimator.KvBytes(Qwen, 4096, "q4_0"), FitEstimator.KvBytes(Qwen, 4096, "Q4_0"));
    [Fact] public void UnknownKvTypeThrows() => Assert.Throws<ArgumentException>(() => FitEstimator.KvBytes(Qwen, 4096, "iq4_nl"));

    [Fact]
    public void PerLayerHeadsSkipRecurrentLayers() =>
        Assert.Equal(8_388_608L, FitEstimator.KvBytes(Qwen with { Layers = 4, HeadCountKv = [8, 0, 8, 0], Recurrent = true }, 1024, "f16"));

    [Fact]
    public void MlaUsesLoraRankPlusRope() =>
        Assert.Equal(287_834_112L, FitEstimator.KvBytes(Qwen with { Layers = 61, HeadCountKv = Enumerable.Repeat(128, 61).ToArray(), KvLoraRank = 512, RopeDimension = 64 }, 4096, "f16"));

    [Fact]
    public void ApproximateForSlidingWindowRecurrentAndMla()
    {
        Assert.False(Qwen.Approximate);
        Assert.True((Qwen with { SlidingWindow = true }).Approximate);
        Assert.True((Qwen with { Recurrent = true }).Approximate);
        Assert.True((Qwen with { KvLoraRank = 512 }).Approximate);
    }

    [Fact]
    public void Q4KmFitsOnSixteenGigabytes()
    {
        var r = FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", 16303L << 20, 64L << 30));
        Assert.Equal(FitLevel.Fits, r.Level);
        Assert.Equal(848_035_840L, r.ComputeBytes);
        Assert.Equal(9_365_481_280L, r.TotalBytes);
        Assert.True(r.ContextAware); Assert.False(r.Approximate);
    }

    [Fact]
    public void ExactBoundaryFitsAndOneByteLessIsPartial()
    {
        var total = 9_365_481_280L;
        Assert.Equal(FitLevel.Fits, FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", total, 64L << 30)).Level);
        Assert.Equal(FitLevel.Partial, FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", total - 1, 64L << 30)).Level);
    }

    [Fact]
    public void LargeModelIsPartialWithLayerCount()
    {
        var r = FitEstimator.Estimate(new(20L << 30, Qwen, 16384, "f16", 16L << 30, 64L << 30));
        Assert.Equal(FitLevel.Partial, r.Level); Assert.Equal(22, r.GpuLayers);
    }

    [Fact]
    public void FitsLeavesGpuLayersNull() =>
        Assert.Null(FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", 24L << 30, 64L << 30)).GpuLayers);

    [Fact]
    public void PartialWithNoRoomForComputeClampsToZeroLayers()
    {
        var r = FitEstimator.Estimate(new(20L << 30, Qwen, 16384, "f16", 1L << 30, 64L << 30));
        Assert.Equal(FitLevel.Partial, r.Level); Assert.Equal(0, r.GpuLayers);
    }

    [Fact]
    public void LittleRamMeansDoesNotFit() =>
        Assert.Equal(FitLevel.DoesNotFit, FitEstimator.Estimate(new(20L << 30, Qwen, 16384, "f16", 16L << 30, 8L << 30)).Level);

    [Fact] public void HugeModelDoesNotFit() =>
        Assert.Equal(FitLevel.DoesNotFit, FitEstimator.Estimate(new(80L << 30, Qwen, 16384, "f16", 16L << 30, 64L << 30)).Level);

    [Fact]
    public void WithoutShapeUsesSizeOnly()
    {
        var r = FitEstimator.Estimate(new(5_000_000_000, null, 16384, "f16", 16L << 30, 64L << 30));
        Assert.Null(r.KvBytes); Assert.False(r.ContextAware); Assert.Null(r.GpuLayers);
        Assert.Equal(65_536_000L + (512L << 20), r.ComputeBytes);
    }

    [Fact]
    public void WithoutVramIsUnknownButStillFillsBytes()
    {
        var r = FitEstimator.Estimate(new(1, Qwen, 4096, "f16", null, 64L << 30));
        Assert.Equal(FitLevel.Unknown, r.Level);
        Assert.Equal(1L + r.KvBytes + r.ComputeBytes + FitEstimator.ReserveBytes, r.TotalBytes);
    }

    [Fact]
    public void ContextChoicesAreCappedAtModelMax()
    {
        Assert.Equal([4096], FitEstimator.ContextChoices(4096));
        Assert.Equal([2048], FitEstimator.ContextChoices(2048));
        Assert.Equal(2048, FitEstimator.DefaultContext(2048));
        Assert.Equal(FitEstimator.ContextChoices(null), FitEstimator.ContextChoices(0)); // A zero maximum means the repository did not report one.
        Assert.Equal([4096, 8192, 16384, 32768, 40960], FitEstimator.ContextChoices(40960));
        Assert.Equal(4096, FitEstimator.DefaultContext(4096));
        Assert.Equal(16384, FitEstimator.DefaultContext(40960));
        Assert.Equal(16384, FitEstimator.DefaultContext(null));
        Assert.Equal([4096, 8192, 16384, 32768, 65536, 131072], FitEstimator.ContextChoices(null));
    }
}
