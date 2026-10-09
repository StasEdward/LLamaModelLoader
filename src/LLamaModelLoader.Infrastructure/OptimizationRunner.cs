using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed class OptimizationRunner(ServerController server, ServerProbe probe,
    Func<CancellationToken, Task<(IReadOnlyList<GpuStatistics> Gpus, string? Error)>>? gpuReader = null)
{
    private readonly Func<CancellationToken, Task<(IReadOnlyList<GpuStatistics> Gpus, string? Error)>> _readGpus = gpuReader ?? StatisticsClient.ReadGpusAsync;

    public async Task<OptimizationReport> RunAsync(AppSettings settings, IReadOnlyList<OptimizationCandidate> candidates,
        OptimizationOptions options, IProgress<string>? progress, CancellationToken token, Func<bool>? restoreAllowed = null)
    {
        options.Validate();
        if (candidates.Count is < 1 or > 12) throw new ArgumentException("Choose between 1 and 12 candidates.");
        if (server.Status.State is ServerState.Starting or ServerState.Stopping) throw new InvalidOperationException("Wait for the server to finish changing state.");
        var capabilities = await probe.InspectAsync(settings.ServerPath, token);
        foreach (var candidate in candidates)
        {
            capabilities.Validate(Arguments.Build(settings, candidate.Profile));
            if (candidate.Profile.Options.ContextSize != options.ContextSize || candidate.Profile.Options.Parallel != 1)
                throw new ArgumentException("Every candidate must use the requested context and one slot.");
            if (ModelCatalog.Inspect(candidate.Profile.ModelPath).Error is { } error) throw new IOException(error);
        }
        if (options.GpuReserveMiB > 0)
        {
            var telemetry = await _readGpus(token);
            if (telemetry.Error is not null || telemetry.Gpus.Count == 0 || telemetry.Gpus.Any(g => g.TotalMiB is null || g.UsedMiB is null))
                throw new InvalidOperationException("A GPU reserve requires NVIDIA memory telemetry. Set reserve to 0 to run without this constraint.");
        }
        var original = server.Status.State == ServerState.Ready ? server.RunningConfiguration : null;
        var started = DateTimeOffset.Now;
        List<OptimizationResult> results = [];
        bool canceled = false;
        string? restorationError = null;
        using var benchmark = new BenchmarkClient();
        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var candidate = candidates[index];
                progress?.Report($"{index + 1}/{candidates.Count} · Loading {candidate.Label}");
                List<BenchmarkSample> samples = [];
                var memory = new MemoryMeasurements();
                string? failure = null;
                try
                {
                    await server.StopAsync();
                    token.ThrowIfCancellationRequested();
                    await server.StartAsync(settings, candidate.Profile, token);
                    await WaitReadyAsync(settings.StartupTimeoutSeconds, token);
                    var status = server.Status;
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                    await benchmark.VerifyContextAsync(status.BaseUrl!, options.ContextSize, requestTimeout.Token);
                    await SampleMemoryAsync(status, memory, token);
                    if (options.GpuReserveMiB > 0 && (memory.TelemetryMissing || memory.MinFree is null || memory.MinFree < options.GpuReserveMiB))
                        throw new InvalidOperationException("GPU reserve was not met after loading or could not be verified; result excluded.");
                    using var sampling = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var samplingTask = PollMemoryAsync(status, memory, sampling.Token);
                    try
                    {
                        progress?.Report($"{index + 1}/{candidates.Count} · Warming up {candidate.Label}");
                        await benchmark.MeasureAsync(status.BaseUrl!, options with { GeneratedTokens = 16 }, token);
                        for (var run = 0; run < options.Repetitions; run++)
                        {
                            progress?.Report($"{index + 1}/{candidates.Count} · {candidate.Label} · Measurement {run + 1}/{options.Repetitions}");
                            samples.Add(await benchmark.MeasureAsync(status.BaseUrl!, options, token));
                        }
                    }
                    finally { sampling.Cancel(); await samplingTask; }
                    await SampleMemoryAsync(status, memory, token);
                    if (samples.Select(s => s.PromptTokens).Distinct().Count() != 1)
                        failure = "Prompt token counts changed between repetitions; result excluded.";
                    if (options.GpuReserveMiB > 0 && (memory.TelemetryMissing || memory.MinFree is null || memory.MinFree < options.GpuReserveMiB))
                        failure = "GPU reserve was not met or could not be verified; result excluded.";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failure = ex is OperationCanceledException ? "Benchmark request timed out." : ex.Message; }
                results.Add(new(candidate, samples, memory.PeakGpu, memory.MinFree, memory.PeakRam, failure));
                progress?.Report($"{candidate.Label}: " + (failure ?? "complete"));
            }
            // Across candidates too: an unexpectedly different prompt is not a fair comparison.
            var expectedPrompt = results.FirstOrDefault(r => r.Eligible)?.Samples[0].PromptTokens;
            for (var i = 0; i < results.Count; i++)
                if (results[i].Eligible && results[i].Samples.Any(s => s.PromptTokens != expectedPrompt))
                    results[i] = results[i] with { Error = "Prompt token count differs from the other candidates; result excluded." };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        finally
        {
            progress?.Report("Stopping test server and restoring the previous session…");
            try
            {
                await server.StopAsync();
                if (original is not null && (restoreAllowed?.Invoke() ?? true))
                {
                    await server.StartAsync(original.Settings, original.Profiles[0]);
                    await WaitReadyAsync(original.Settings.StartupTimeoutSeconds, CancellationToken.None, restoreAllowed);
                }
            }
            catch (Exception ex)
            {
                restorationError = "Could not restore the previous session: " + ex.Message;
                try { await server.StopAsync(); } catch (Exception stopError) { restorationError += " Cleanup: " + stopError.Message; }
            }
        }
        return new(started, options, results, canceled, restorationError, capabilities.Version);
    }

    private async Task WaitReadyAsync(int timeoutSeconds, CancellationToken token, Func<bool>? continueAllowed = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 5));
        while (server.Status.State == ServerState.Starting)
        {
            if (!(continueAllowed?.Invoke() ?? true)) throw new OperationCanceledException("Application is shutting down.");
            await Task.Delay(100, timeout.Token);
        }
        if (server.Status.State != ServerState.Ready) throw new InvalidOperationException(server.Status.Message);
    }

    private sealed class MemoryMeasurements
    {
        public double? PeakGpu, MinFree, PeakRam;
        public bool TelemetryMissing;
    }
    private async Task SampleMemoryAsync(ServerStatus status, MemoryMeasurements memory, CancellationToken token)
    {
        var (gpus, error) = await _readGpus(token);
        if (error is not null || gpus.Count == 0 || gpus.Any(g => g.UsedMiB is null || g.TotalMiB is null)) memory.TelemetryMissing = true;
        else
        {
            var used = gpus.Sum(g => g.UsedMiB!.Value);
            var free = gpus.Min(g => g.TotalMiB!.Value - g.UsedMiB!.Value);
            memory.PeakGpu = Math.Max(memory.PeakGpu ?? 0, used);
            memory.MinFree = Math.Min(memory.MinFree ?? double.MaxValue, free);
        }
        if (await server.ReadResourcesAsync(status, token) is { } resources)
            memory.PeakRam = Math.Max(memory.PeakRam ?? 0, resources.WorkingSetBytes / 1048576d);
    }
    private async Task PollMemoryAsync(ServerStatus status, MemoryMeasurements memory, CancellationToken token)
    {
        try
        {
            while (true) { await Task.Delay(1000, token); await SampleMemoryAsync(status, memory, token); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
