using LLamaModelLoader.Core;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class HuggingFaceTests
{
    [Theory]
    [InlineData("unsloth/Qwen3-8B-GGUF", true)]
    [InlineData("bartowski/Llama-3.3-70B-Instruct-GGUF", true)]
    [InlineData("no-slash", false)]
    [InlineData("a/b/c", false)]
    [InlineData("../evil", false)]
    [InlineData("owner/..", false)]
    [InlineData("-owner/repo", false)]
    public void ValidatesRepoIds(string id, bool valid) => Assert.Equal(valid, HfNames.IsValidRepoId(id));

    [Theory]
    [InlineData("Qwen3-8B-Q4_K_M.gguf", true)]
    [InlineData("IQ2_XS/Model-IQ2_XS-00001-of-00002.gguf", true)]
    [InlineData("../Model.gguf", false)]
    [InlineData("sub/../../Model.gguf", false)]
    [InlineData("CON.gguf", false)]
    [InlineData("sub\\Model.gguf", false)]
    [InlineData("/abs/Model.gguf", false)]
    [InlineData("C:/Model.gguf", false)]
    [InlineData("Model.bin", false)]
    [InlineData("Model .gguf", true)]
    public void RejectsUnsafeRepoPaths(string path, bool safe) => Assert.Equal(safe, HfNames.IsSafeRepoPath(path));

    [Theory]
    [InlineData("Qwen3-8B-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("Qwen3-8B-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("IQ2_XS/Model-IQ2_XS-00001-of-00002.gguf", "IQ2_XS")]
    [InlineData("model-bf16.gguf", "BF16")]
    [InlineData("Model-Q8_0.gguf", "Q8_0")]
    [InlineData("model.gguf", "model")]
    public void ParsesQuantizationLabels(string path, string label) => Assert.Equal(label, HfNames.QuantLabel(path));

    [Fact]
    public void GroupsShardsAndDropsIncompleteSets()
    {
        HfFile F(string p, long s) => new(p, s, new string('a', 64));
        var groups = HfNames.GroupQuantizations([
            F("Q8_0/M-Q8_0-00001-of-00002.gguf", 10), F("Q8_0/M-Q8_0-00002-of-00002.gguf", 5),
            F("Q4_K_M/M-Q4_K_M-00001-of-00003.gguf", 4), F("Q4_K_M/M-Q4_K_M-00003-of-00003.gguf", 4), // part 2 missing
            F("M-Q2_K.gguf", 3), F("mmproj-M-F16.gguf", 1)]);
        Assert.Equal(["Q2_K", "Q8_0"], groups.Select(g => g.Label));
        Assert.Equal(15, groups[1].TotalBytes);
        Assert.Equal("Q8_0/M-Q8_0-00001-of-00002.gguf", groups[1].FirstPath);
        Assert.Equal(2, groups[1].Parts.Count);
    }

    [Theory]
    [InlineData("image-text-to-text", new string[0], true)]
    [InlineData("text-generation", new[] { "gguf", "vision" }, true)]
    [InlineData("text-generation", new[] { "multimodal" }, true)]
    [InlineData("text-generation", new[] { "gguf" }, false)]
    [InlineData(null, new string[0], false)]
    public void DetectsVisionModels(string? pipeline, string[] tags, bool vision) =>
        Assert.Equal(vision, new HfModelSummary("a/b", "a", 0, 0, null, false, pipeline, tags).IsVision);

    [Theory]
    [InlineData(HfSort.Downloads, "downloads")]
    [InlineData(HfSort.Likes, "likes")]
    [InlineData(HfSort.LastModified, "lastModified")]
    [InlineData(HfSort.Trending, "trendingScore")]
    public void MapsSortKeys(HfSort sort, string key) => Assert.Equal(key, HfNames.SortKey(sort));
}
