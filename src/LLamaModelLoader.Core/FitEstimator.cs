namespace LLamaModelLoader.Core;

public sealed record GgufKvShape(string Architecture, int Layers, IReadOnlyList<int> HeadCountKv, int KeyLength, int ValueLength, ulong? VocabularySize, int? KvLoraRank, int? RopeDimension, bool SlidingWindow, bool Recurrent)
{
    public bool Approximate => SlidingWindow || Recurrent || KvLoraRank is not null;
}

public sealed record FitInput(long WeightBytes, GgufKvShape? Shape, int ContextSize, string KvType, long? VramBytes, long RamBytes);

public enum FitLevel { Unknown, Fits, Partial, DoesNotFit }

public sealed record FitResult(FitLevel Level, long WeightBytes, long? KvBytes, long ComputeBytes, long ReserveBytes, long TotalBytes, int? GpuLayers, bool ContextAware, bool Approximate);

public static class FitEstimator
{
    public const long ReserveBytes = 1024L << 20;
    private const long HeldBackRamBytes = 8L << 30;
    private const long DefaultVocabulary = 32000;
    private static readonly int[] Contexts = [4096, 8192, 16384, 32768, 65536, 131072];

    public static FitResult Estimate(FitInput input)
    {
        var shape = input.Shape;
        long? kv = shape is null ? null : KvBytes(shape, input.ContextSize, input.KvType);
        var compute = checked((long)(shape?.VocabularySize ?? DefaultVocabulary) * 512 * 4 + (512L << 20));
        var weightsAndKv = checked(input.WeightBytes + (kv ?? 0));
        var total = checked(weightsAndKv + compute + ReserveBytes);
        var level = FitLevel.Unknown;
        int? gpuLayers = null;
        if (input.VramBytes is { } vram)
        {
            var spill = Math.Max(0, input.RamBytes - HeldBackRamBytes);
            if (total <= vram) level = FitLevel.Fits;
            else if (checked(weightsAndKv + compute) <= checked(vram + spill))
            {
                level = FitLevel.Partial;
                // each layer's KV cache lives on the layer's device, so a GPU layer costs its weights plus its KV
                if (shape is { Layers: > 0 })
                {
                    var perLayer = weightsAndKv / (double)shape.Layers;
                    var room = vram - ReserveBytes - compute;
                    gpuLayers = room <= 0 || perLayer <= 0 ? 0 : (int)Math.Clamp(Math.Floor(room / perLayer), 0, shape.Layers - 1);
                }
            }
            else level = FitLevel.DoesNotFit;
        }
        return new(level, input.WeightBytes, kv, compute, ReserveBytes, total, gpuLayers, shape is not null, shape?.Approximate ?? false);
    }

    public static long KvBytes(GgufKvShape shape, int context, string kvType)
    {
        var (numerator, denominator) = BytesPerElement(kvType);
        checked
        {
            var elements = shape.KvLoraRank is { } rank && shape.RopeDimension is { } rope
                ? (long)context * shape.Layers * (rank + rope)
                : (long)context * shape.HeadCountKv.Sum(h => (long)h) * (shape.KeyLength + shape.ValueLength);
            return elements * numerator / denominator;
        }
    }

    public static IReadOnlyList<int> ContextChoices(ulong? modelMax)
    {
        if (modelMax is not { } max) return Contexts;
        if (max <= (ulong)Contexts[0]) return [Contexts[0]];
        return [.. Contexts.Where(c => (ulong)c < max), (int)Math.Min(max, int.MaxValue)];
    }

    public static int DefaultContext(ulong? modelMax) => ContextChoices(modelMax).Where(c => c <= 16384).Max();

    private static (long Numerator, long Denominator) BytesPerElement(string kvType) => kvType.ToLowerInvariant() switch
    {
        "f16" or "bf16" => (2, 1),
        "f32" => (4, 1),
        "q8_0" => (34, 32),
        "q4_0" => (18, 32),
        "q4_1" => (20, 32),
        "q5_0" => (22, 32),
        "q5_1" => (24, 32),
        _ => throw new ArgumentException($"Unsupported KV cache type '{kvType}'.", nameof(kvType)),
    };
}
