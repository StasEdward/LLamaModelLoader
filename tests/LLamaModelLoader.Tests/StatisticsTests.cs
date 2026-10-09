using System.Globalization;
using System.Net;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class StatisticsTests
{
    [Fact]
    public void SessionGenerationAverageUsesTotalTimeAndSurvivesIdlePolls()
    {
        var metrics = new Dictionary<string, double> {
            ["llamacpp:tokens_predicted_total"] = 100,
            ["llamacpp:tokens_predicted_seconds_total"] = 2,
            ["llamacpp:predicted_tokens_seconds"] = 50 };
        var first = new StatisticsSnapshot(metrics, [], []);
        Assert.Equal(50d, first.SessionGenerationTokensPerSecond);
        metrics["llamacpp:predicted_tokens_seconds"] = 0;
        var idle = new StatisticsSnapshot(metrics, [], []);
        Assert.Equal(50d, idle.SessionGenerationTokensPerSecond);
        // Another 100 tokens take 8 seconds: use 200/10, not the mean of 50 and 12.5.
        metrics["llamacpp:tokens_predicted_total"] = 200;
        metrics["llamacpp:tokens_predicted_seconds_total"] = 10;
        Assert.Equal(20d, new StatisticsSnapshot(metrics, [], []).SessionGenerationTokensPerSecond);
        Assert.Null(new StatisticsSnapshot(new Dictionary<string, double>(), [], []).SessionGenerationTokensPerSecond);
        metrics["llamacpp:tokens_predicted_total"] = 0;
        metrics["llamacpp:tokens_predicted_seconds_total"] = 0;
        Assert.Null(new StatisticsSnapshot(metrics, [], []).SessionGenerationTokensPerSecond);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(-1, 1)]
    public void SessionGenerationAverageRejectsInvalidCounters(double tokens, double seconds)
    {
        var snapshot = new StatisticsSnapshot(new Dictionary<string, double> {
            ["llamacpp:tokens_predicted_total"] = tokens,
            ["llamacpp:tokens_predicted_seconds_total"] = seconds }, [], []);
        Assert.Null(snapshot.SessionGenerationTokensPerSecond);
    }

    [Fact]
    public void PrometheusNumbersAreInvariantAndMissingValuesStayUnknown()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var values = StatisticsClient.ParseMetrics("""
                # HELP llamacpp:predicted_tokens_seconds average
                llamacpp:predicted_tokens_seconds{model="Qwen test"} 1.25e+2 10000
                llamacpp:prompt_tokens_total 32000
                llamacpp:requests_processing 0
                llamacpp:prompt_tokens_seconds NaN
                llamacpp:tokens_predicted_total +Inf
                llamacpp:requests_deferred invalid
                """);
            Assert.Equal(125, values["llamacpp:predicted_tokens_seconds"]);
            Assert.Equal(0, values["llamacpp:requests_processing"]);
            Assert.Equal(3, values.Count);
            Assert.Null(new StatisticsSnapshot(values, [], []).Metric("tokens_predicted_total"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void SlotsSupportObjectArrayAndMissingTokenDetails()
    {
        var slots = StatisticsClient.ParseSlots("""
            [{"id":0,"is_processing":true,"n_ctx":4096,"n_prompt_tokens":100,"next_token":{"n_decoded":12}},
             {"id":1,"is_processing":false,"next_token":[{"n_decoded":4}]},
             {"id":2,"is_processing":false}]
            """);
        Assert.Equal(3, slots.Count); Assert.True(slots[0].Processing);
        Assert.Equal(12, slots[0].GeneratedTokens); Assert.Equal(4, slots[1].GeneratedTokens);
        Assert.Null(slots[2].GeneratedTokens); Assert.Null(slots[2].Context);
    }

    [Fact]
    public void GpuCsvPreservesMultipleDevicesAndUnsupportedValues()
    {
        var gpus = StatisticsClient.ParseGpus("0, NVIDIA GeForce RTX 5080, 94, 15069, 16303, 58\n1, \"GPU, special\", [N/A], 100, 8192, [Not Supported]\n");
        Assert.Equal(2, gpus.Count); Assert.Equal(15069, gpus[0].UsedMiB);
        Assert.Equal("GPU, special", gpus[1].Name); Assert.Null(gpus[1].Temperature); Assert.Null(gpus[1].Utilization);
    }

    [Fact]
    public async Task DisabledMetricsDoNotHideWorkingSlotsOrGpu()
    {
        using var client = new StatisticsClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/metrics"
            ? new HttpResponseMessage(HttpStatusCode.NotImplemented)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":0,\"is_processing\":true}]") })), Gpus);
        var result = await client.ReadAsync("http://127.0.0.1:12345", CancellationToken.None);
        Assert.Contains("--metrics", result.MetricsError); Assert.Null(result.SlotsError);
        Assert.Single(result.Slots); Assert.Single(result.Gpus); Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task MalformedResponsesAreUnavailableRatherThanZero()
    {
        using var client = new StatisticsClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not telemetry") })), Gpus);
        var result = await client.ReadAsync("http://127.0.0.1:12345", CancellationToken.None);
        Assert.NotNull(result.MetricsError); Assert.NotNull(result.SlotsError); Assert.Single(result.Gpus);
        Assert.Null(result.Metric("requests_processing"));
    }

    [Fact]
    public async Task ClosingCancelsRequestsAndStoppedServerMakesNoHttpRequests()
    {
        var calls = 0;
        using var client = new StatisticsClient(new Handler(async (_, token) => { Interlocked.Increment(ref calls); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }), Gpus);
        var stopped = await client.ReadAsync(null, CancellationToken.None);
        Assert.Equal(0, calls); Assert.NotNull(stopped.MetricsError);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync("http://127.0.0.1:12345", cancel.Token));
        Assert.Equal(2, calls);
    }

    private static Task<(IReadOnlyList<GpuStatistics>, string?)> Gpus(CancellationToken _) => Task.FromResult<(IReadOnlyList<GpuStatistics>, string?)>(([new("0", "Test GPU", 50, 100, 200, 60)], null));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
