using System.Net;
using System.Text;
using System.Text.Json;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class OptimizationTests
{
    private static HashSet<string> Flags => OptionCatalog.All.Select(o => o.Flag).ToHashSet();
    [Fact]
    public void CandidateSearchIsBoundedAndPreservesSourcePrecisionAndContext()
    {
        var source = new ModelProfile { Name = "Test", Options = new() { ContextSize = 32768, Parallel = 4, CacheTypeK = "f16", CacheTypeV = "f16", SpecType = "none" } };
        var original = JsonSerializer.Serialize(source);
        var plan = OptimizationPlan.Create(source, new() { ContextSize = 32768 }, Flags);
        Assert.InRange(plan.Count, 2, 5);
        Assert.All(plan, c => { Assert.Equal(32768, c.Profile.Options.ContextSize); Assert.Equal(1, c.Profile.Options.Parallel);
            Assert.Equal("f16", c.Profile.Options.CacheTypeK); Assert.Equal("f16", c.Profile.Options.CacheTypeV); Assert.Equal("none", c.Profile.Options.SpecType); });
        Assert.Equal(original, JsonSerializer.Serialize(source));
        var extended = OptimizationPlan.Create(source, new() { TuneCache = true, TuneMtp = true }, Flags);
        Assert.Contains(extended, c => c.Profile.Options.SpecType == "draft-mtp");
        Assert.Contains(extended, c => c.Profile.Options.CacheTypeK == "q4_0");
        Assert.InRange(extended.Count, 2, 9);
        Assert.Single(OptimizationPlan.Create(source, new(), new HashSet<string>()));
    }
    [Fact]
    public void RankingUsesMediansAndExcludesFailedCandidates()
    {
        var fast = new OptimizationResult(new("fast", new()), [new(100, 50, 20, 64, 128), new(100, 60, 30, 64, 128), new(100, 10000, 1000, 64, 128)], null, null, null, null);
        var prompt = new OptimizationResult(new("prompt", new()), [new(1000, 20, 10, 64, 128)], null, null, null, null);
        Assert.Equal(60, fast.GenerationSpeed); Assert.Equal(30, fast.FirstTokenMilliseconds);
        Assert.Same(fast, OptimizationPlan.Best([fast, prompt], OptimizationGoal.Generation));
        Assert.Same(prompt, OptimizationPlan.Best([fast, prompt], OptimizationGoal.PromptProcessing));
        Assert.Same(prompt, OptimizationPlan.Best([fast, prompt], OptimizationGoal.Balanced));
        Assert.Same(prompt, OptimizationPlan.Best([fast with { Error = "reserve" }, prompt], OptimizationGoal.Generation));
        Assert.Null(OptimizationPlan.Best([fast with { Error = "failed" }], OptimizationGoal.Generation));
    }
    [Fact]
    public async Task BenchmarkUsesUncachedFixedWorkAndNativeTimings()
    {
        using var client = new BenchmarkClient(new Handler(async request =>
        {
            Assert.Equal("/completion", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("cache_prompt").GetBoolean());
            Assert.True(json.RootElement.GetProperty("ignore_eos").GetBoolean());
            Assert.Equal(42, json.RootElement.GetProperty("seed").GetInt32());
            return Response(128, 0, false);
        }));
        var sample = await client.MeasureAsync("http://localhost", new(), CancellationToken.None);
        Assert.Equal(640, sample.PromptTokensPerSecond); Assert.Equal(64, sample.GenerationTokensPerSecond);
        Assert.True(sample.FirstTokenMilliseconds >= 0);
    }
    [Theory]
    [InlineData(127, 0, false)]
    [InlineData(128, 1, false)]
    [InlineData(128, 0, true)]
    public async Task InvalidWorkloadsAreExcluded(int tokens, int cached, bool truncated)
    {
        using var client = new BenchmarkClient(new Handler(_ => Task.FromResult(Response(tokens, cached, truncated))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.MeasureAsync("http://localhost", new(), CancellationToken.None));
    }
    [Fact]
    public async Task BenchmarkCancellationReachesHttpRequest()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var client = new BenchmarkClient(new CancelHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.MeasureAsync("http://localhost", new(), cancel.Token));
    }
    [Fact]
    public async Task ContextMustBeVerifiedBeforeMeasurement()
    {
        using var client = new BenchmarkClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("[{\"id\":0,\"n_ctx\":4096}]") })));
        await client.VerifyContextAsync("http://localhost", 4096, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.VerifyContextAsync("http://localhost", 8192, CancellationToken.None));
    }
    private static HttpResponseMessage Response(int tokens, int cached, bool truncated) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"content\":\"hello\",\"stop\":false}\n\n" + "data: " + JsonSerializer.Serialize(new
        { stop = true, truncated, timings = new { prompt_n = 64, prompt_ms = 100, predicted_n = tokens, predicted_ms = 2000, cache_n = cached } }) + "\n\n", Encoding.UTF8, "text/event-stream")
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request); }
    private sealed class CancelHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
}
