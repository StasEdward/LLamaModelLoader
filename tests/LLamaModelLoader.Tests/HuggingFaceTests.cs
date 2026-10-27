using System.Net;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
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

    [Fact]
    public void ParsesSearchResultsAndSkipsMalformedEntries()
    {
        var models = HuggingFaceClient.ParseSearch("""
            [
              {"id":"unsloth/Qwen3-VL-8B-GGUF","author":"unsloth","downloads":1200,"likes":34,"lastModified":"2026-03-01T10:20:30.000Z","gated":false,"pipeline_tag":"image-text-to-text","tags":["gguf","vision"]},
              {"id":"meta/Llama-GGUF","author":"meta","downloads":5,"likes":1,"gated":"manual","tags":["gguf"]},
              {"author":"nobody","downloads":1},
              {"id":"../x","author":"evil"}
            ]
            """);
        Assert.Equal(["unsloth/Qwen3-VL-8B-GGUF", "meta/Llama-GGUF"], models.Select(m => m.Id));
        Assert.False(models[0].Gated); Assert.True(models[1].Gated);
        Assert.True(models[0].IsVision); Assert.False(models[1].IsVision);
        Assert.Equal(1200, models[0].Downloads); Assert.Equal(34, models[0].Likes);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 10, 20, 30, TimeSpan.Zero), models[0].LastModified);
        Assert.Null(models[1].LastModified);
    }

    [Fact]
    public void ParsesDetailsWithLicenseShaAndQuantizations()
    {
        var sha = new string('a', 40); var digest = new string('b', 64);
        var details = HuggingFaceClient.ParseDetails($$$"""
            {
              "id":"unsloth/Qwen3-8B-GGUF","sha":"{{{sha}}}","gated":false,
              "gguf":{"total":8190735360,"architecture":"qwen3","context_length":40960,"chat_template":"ignored"},
              "tags":["gguf","license:apache-2.0"],
              "siblings":[
                {"rfilename":".gitattributes","size":1500},
                {"rfilename":"Qwen3-8B-Q4_K_M.gguf","size":5027783488,"lfs":{"sha256":"{{{digest}}}","size":5027783488}},
                {"rfilename":"Qwen3-8B-Q5_K_M.gguf","size":5851000000},
                {"rfilename":"Qwen3-8B-Q8_0.gguf","size":0,"lfs":{"sha256":"{{{digest}}}","size":0}}
              ]
            }
            """);
        Assert.Equal("unsloth/Qwen3-8B-GGUF", details.Id); Assert.Equal(sha, details.Sha); Assert.False(details.Gated);
        Assert.Equal("qwen3", details.Architecture); Assert.Equal("apache-2.0", details.License);
        Assert.Equal(8190735360UL, details.Parameters); Assert.Equal(40960UL, details.ContextLength);
        var quant = Assert.Single(details.Quantizations);
        Assert.Equal("Q4_K_M", quant.Label); Assert.Equal(5027783488, quant.TotalBytes); Assert.Equal(digest, quant.Parts[0].Sha256);
    }

    [Theory]
    [InlineData("""{"id":"a/b","gated":false,"siblings":[]}""")]
    [InlineData("""{"id":"a/b","sha":"abc","gated":false,"siblings":[]}""")]
    [InlineData("""{"id":"../b","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{ nope""")]
    public void RejectsUnrecognizedDetails(string json) => Assert.Throws<InvalidDataException>(() => HuggingFaceClient.ParseDetails(json));

    [Fact]
    public void RejectsSearchThatIsNotAnArray() => Assert.Throws<InvalidDataException>(() => HuggingFaceClient.ParseSearch("""{"error":"x"}"""));

    [Fact]
    public void NextLinkAcceptsOnlyHuggingFaceApi()
    {
        Assert.Equal("https://huggingface.co/api/models?cursor=x", HuggingFaceClient.ParseNextLink("""<https://huggingface.co/api/models?cursor=x>; rel="next" """));
        Assert.Equal("https://huggingface.co/api/models?cursor=y", HuggingFaceClient.ParseNextLink("""<https://huggingface.co/api/models?cursor=p>; rel="prev", <https://huggingface.co/api/models?cursor=y>; rel="next" """));
        Assert.Null(HuggingFaceClient.ParseNextLink("""<https://evil.example/api/models?x>; rel="next" """));
        Assert.Null(HuggingFaceClient.ParseNextLink("""<http://huggingface.co/api/models?x>; rel="next" """));
        Assert.Null(HuggingFaceClient.ParseNextLink("""<https://huggingface.co/other?x>; rel="next" """));
        Assert.Null(HuggingFaceClient.ParseNextLink("""<https://huggingface.co/api/models?x>; rel="prev" """));
        Assert.Null(HuggingFaceClient.ParseNextLink(null));
    }

    [Fact]
    public async Task SearchBuildsQueryAndReportsRateLimit()
    {
        var calls = 0; string? uri = null, agent = null;
        using var http = Http((request, _) =>
        {
            if (++calls == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            uri = request.RequestUri!.AbsoluteUri; agent = request.Headers.UserAgent.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"id":"a/b","author":"a","downloads":1,"likes":2,"gated":false,"tags":[]}]""") };
            response.Headers.TryAddWithoutValidation("Link", """<https://huggingface.co/api/models?cursor=n>; rel="next" """);
            return Task.FromResult(response);
        });
        var client = new HuggingFaceClient(http);
        var (models, next) = await client.SearchAsync("qwen 3", HfSort.Downloads, null, default);
        Assert.Equal("https://huggingface.co/api/models?search=qwen%203&filter=gguf&sort=downloads&limit=30&full=false", uri);
        Assert.Contains("LLamaModelLoader/1.0", agent);
        Assert.Equal("a/b", Assert.Single(models).Id); Assert.Equal("https://huggingface.co/api/models?cursor=n", next);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("qwen 3", HfSort.Downloads, next, default));
        Assert.Contains("rate limit", ex.Message);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task SearchRequestsNextPageVerbatimOnlyWhenItIsAHuggingFaceApiUrl()
    {
        var requested = new List<string>();
        using var http = Http((request, _) => { requested.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }); });
        var client = new HuggingFaceClient(http);
        await client.SearchAsync("q", HfSort.Likes, "https://huggingface.co/api/models?cursor=zz", default);
        await client.SearchAsync("", HfSort.Likes, "https://evil.example/api/models?cursor=zz", default);
        Assert.Equal(["https://huggingface.co/api/models?cursor=zz", "https://huggingface.co/api/models?filter=gguf&sort=likes&limit=30&full=false"], requested);
    }

    [Fact]
    public async Task DetailsRequestUsesBlobsAndExpand()
    {
        string? uri = null;
        var body = $$"""{"id":"unsloth/Qwen3-8B-GGUF","sha":"{{new string('c', 40)}}","siblings":[]}""";
        using var http = Http((request, _) =>
        {
            uri = request.RequestUri!.OriginalString;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
        var details = await new HuggingFaceClient(http).GetDetailsAsync("unsloth/Qwen3-8B-GGUF", default);
        Assert.Equal("https://huggingface.co/api/models/unsloth/Qwen3-8B-GGUF?blobs=true&expand[]=gguf&expand[]=siblings&expand[]=gated&expand[]=sha&expand[]=tags", uri);
        Assert.False(details.Gated); Assert.Empty(details.Quantizations);

        var calls = 0;
        using var guarded = Http((_, _) => { calls++; throw new Exception("Request was made"); });
        await Assert.ThrowsAsync<ArgumentException>(() => new HuggingFaceClient(guarded).GetDetailsAsync("../evil", default));
        Assert.Equal(0, calls);
    }

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
