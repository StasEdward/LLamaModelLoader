using System.Text.Json;

namespace LLamaModelLoader.Core;

public enum OptimizationGoal { Balanced, Generation, PromptProcessing }
public sealed record OptimizationOptions
{
    public int ContextSize { get; init; } = 8192;
    public int GeneratedTokens { get; init; } = 128;
    public int Repetitions { get; init; } = 3;
    public int RequestTimeoutSeconds { get; init; } = 180;
    public int GpuReserveMiB { get; init; } = 1024;
    public OptimizationGoal Goal { get; init; }
    public bool TuneGpuLayers { get; init; } = true;
    public bool TuneBatch { get; init; } = true;
    public bool TuneCache { get; init; }
    public bool TuneMtp { get; init; }
    public string Prompt { get; init; } = string.Join('\n', Enumerable.Repeat(
        "A local inference server processes requests on a workstation. Explain how to measure latency, throughput, memory consumption, and reproducibility. Include concrete examples and continue with a detailed numbered test plan.", 16));
    public void Validate()
    {
        if (ContextSize is < 512 or > 2_097_152 || GeneratedTokens is < 16 or > 4096 || GeneratedTokens >= ContextSize ||
            Repetitions is < 1 or > 5 || RequestTimeoutSeconds is < 5 or > 3600 || GpuReserveMiB is < 0 or > 1_048_576 ||
            string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > 65536 || !Enum.IsDefined(Goal))
            throw new ArgumentException("Check context (512–2097152), output tokens (16–4096, below context), repetitions (1–5), timeout (5–3600 s), reserve, and prompt (1–65536 characters).");
    }
}

public sealed record OptimizationCandidate(string Label, ModelProfile Profile);
public sealed record BenchmarkSample(double PromptTokensPerSecond, double GenerationTokensPerSecond,
    double FirstTokenMilliseconds, int PromptTokens, int GeneratedTokens);
public sealed record OptimizationResult(OptimizationCandidate Candidate, IReadOnlyList<BenchmarkSample> Samples,
    double? PeakGpuMiB, double? MinimumGpuFreeMiB, double? PeakRamMiB, string? Error)
{
    public bool Eligible => Error is null && Samples.Count > 0;
    public double PromptSpeed => Median(Samples.Select(s => s.PromptTokensPerSecond));
    public double GenerationSpeed => Median(Samples.Select(s => s.GenerationTokensPerSecond));
    public double FirstTokenMilliseconds => Median(Samples.Select(s => s.FirstTokenMilliseconds));
    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values.Length == 0 ? 0 : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }
}
public sealed record OptimizationReport(DateTimeOffset StartedAt, OptimizationOptions Options,
    IReadOnlyList<OptimizationResult> Results, bool Canceled, string? RestorationError, string ServerVersion);

public static class OptimizationPlan
{
    public static ModelProfile Copy(ModelProfile profile) => JsonSerializer.Deserialize<ModelProfile>(JsonSerializer.Serialize(profile))!;

    // A bounded search: keep a comparable baseline, then vary one group at a time.
    // Precision and MTP changes are opt-in; context and slot count are fixed for every candidate.
    public static IReadOnlyList<OptimizationCandidate> Create(ModelProfile source, OptimizationOptions options, ISet<string> flags)
    {
        options.Validate();
        var baseline = Copy(source); SpeculativeOptions.ImportExtraArguments(baseline);
        baseline.Options.ContextSize = options.ContextSize; baseline.Options.Parallel = 1;
        List<OptimizationCandidate> result = [new("Baseline · fixed context, 1 slot", baseline)];
        HashSet<string> seen = [JsonSerializer.Serialize(baseline.Options)];
        void Add(string label, Action<LlamaOptions> change, params string[] required)
        {
            if (required.Any(f => !flags.Contains(f))) return;
            var profile = Copy(baseline); change(profile.Options);
            if (seen.Add(JsonSerializer.Serialize(profile.Options))) result.Add(new(label, profile));
        }
        if (options.TuneGpuLayers)
        {
            Add("GPU layers: auto", o => o.GpuLayers = "auto", "--gpu-layers");
            Add("GPU layers: all", o => o.GpuLayers = "all", "--gpu-layers");
        }
        if (options.TuneBatch)
        {
            Add("Batch 512 / microbatch 128", o => { o.BatchSize = 512; o.MicroBatchSize = 128; }, "--batch-size", "--ubatch-size");
            Add("Batch 2048 / microbatch 512", o => { o.BatchSize = 2048; o.MicroBatchSize = 512; }, "--batch-size", "--ubatch-size");
        }
        if (options.TuneCache)
        {
            Add("KV Q8_0 · Flash Attention on", o => { o.CacheTypeK = o.CacheTypeV = "q8_0"; o.FlashAttention = "on"; }, "--cache-type-k", "--cache-type-v", "--flash-attn");
            Add("KV Q4_0 · Flash Attention on", o => { o.CacheTypeK = o.CacheTypeV = "q4_0"; o.FlashAttention = "on"; }, "--cache-type-k", "--cache-type-v", "--flash-attn");
        }
        if (options.TuneMtp)
        {
            Add("MTP off", o => { o.SpecType = "none"; o.SpecDraftNMax = null; o.SpecDraftPMin = null; }, "--spec-type");
            Add("MTP: 2 tokens / p 0.60", o => { o.SpecType = "draft-mtp"; o.SpecDraftNMax = 2; o.SpecDraftPMin = 0.6; }, "--spec-type", "--spec-draft-n-max", "--spec-draft-p-min");
        }
        return result;
    }

    public static OptimizationResult? Best(IEnumerable<OptimizationResult> results, OptimizationGoal goal) =>
        results.Where(r => r.Eligible).OrderByDescending(r => goal switch
        {
            OptimizationGoal.Generation => r.GenerationSpeed,
            OptimizationGoal.PromptProcessing => r.PromptSpeed,
            _ => Math.Sqrt(r.GenerationSpeed * r.PromptSpeed)
        }).FirstOrDefault();
}
