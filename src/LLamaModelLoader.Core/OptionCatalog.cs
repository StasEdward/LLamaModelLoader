using System.Globalization;
using System.Reflection;

namespace LLamaModelLoader.Core;

public sealed record OptionDefinition(string Property, string Group, string Label, string Flag,
    string Hint, double? Minimum = null, double? Maximum = null, string[]? Choices = null,
    string? FalseFlag = null)
{
    public PropertyInfo PropertyInfo => typeof(LlamaOptions).GetProperty(Property)!;
    public string Format(LlamaOptions options) => Convert.ToString(PropertyInfo.GetValue(options), CultureInfo.InvariantCulture) ?? "";

    public void Set(LlamaOptions options, string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) { PropertyInfo.SetValue(options, null); return; }
        var type = Nullable.GetUnderlyingType(PropertyInfo.PropertyType) ?? PropertyInfo.PropertyType;
        object value;
        if (type == typeof(int))
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"{Label}: an integer is required.");
            ValidateNumber(number); value = number;
        }
        else if (type == typeof(double))
        {
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                throw new ArgumentException($"{Label}: a finite number is required.");
            ValidateNumber(number); value = number;
        }
        else if (type == typeof(bool))
        {
            if (!bool.TryParse(text, out var boolean)) throw new ArgumentException($"{Label}: True or False.");
            value = boolean;
        }
        else
        {
            if (Choices is not null && !Choices.Contains(text, StringComparer.Ordinal))
                throw new ArgumentException($"{Label}: choose {string.Join(", ", Choices)}.");
            if (Property == nameof(LlamaOptions.GpuLayers) && text is not ("auto" or "all") &&
                (!int.TryParse(text, out var layers) || layers < 0))
                throw new ArgumentException("GPU layers: a number ≥ 0, auto, or all.");
            value = text;
        }
        PropertyInfo.SetValue(options, value);
    }

    private void ValidateNumber(double number)
    {
        if (Minimum is { } min && number < min || Maximum is { } max && number > max)
            throw new ArgumentException($"{Label}: valid range: {Minimum?.ToString(CultureInfo.InvariantCulture) ?? "−∞"} to {Maximum?.ToString(CultureInfo.InvariantCulture) ?? "+∞"}.");
    }
}

public static class OptionCatalog
{
    public static readonly IReadOnlyList<OptionDefinition> All = [
        new("ContextSize", "General", "Context size, tokens", "--ctx-size", "Leave blank for the server default. 0 uses the model context size.", 0),
        new("GpuLayers", "General", "GPU layers", "--gpu-layers", "A number ≥ 0, auto, or all; leave blank for the server default."),
        new("Threads", "Performance", "CPU threads", "--threads", "Threads used during generation.", 1),
        new("ThreadsBatch", "Performance", "Prompt processing threads", "--threads-batch", "Parallelism for processing input text.", 1),
        new("BatchSize", "Performance", "Batch size", "--batch-size", "Maximum logical batch size.", 1),
        new("MicroBatchSize", "Performance", "Microbatch size", "--ubatch-size", "Maximum physical batch size.", 1),
        new("SpecType", "Speculative decoding", "Method", "--spec-type", "For example: draft-mtp; none disables it. Separate multiple methods with commas. MTP requires compatible model weights."),
        new("SpecDraftNMax", "Speculative decoding", "Maximum draft tokens", "--spec-draft-n-max", "Number of tokens drafted before verification by the main model. For example: 2.", 0),
        new("SpecDraftPMin", "Speculative decoding", "Minimum draft token probability", "--spec-draft-p-min", "Threshold from 0 to 1. For example: 0.60. Leave blank for the server default.", 0, 1),
        new("FlashAttention", "Memory", "Flash Attention", "--flash-attn", "Depends on the model and backend.", Choices: ["auto", "on", "off"]),
        new("CacheTypeK", "Memory", "K cache type", "--cache-type-k", "Supported types depend on the server build.", Choices: ["f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl"]),
        new("CacheTypeV", "Memory", "V cache type", "--cache-type-v", "Quantized V cache may require Flash Attention.", Choices: ["f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl"]),
        new("LoadMode", "Memory", "Memory loading", "--load-mode", "For recent llama.cpp builds.", Choices: ["auto", "none", "mmap", "mlock", "mmap+mlock", "dio"]),
        new("Fit", "Memory", "Fit to GPU memory", "--fit", "Only adjusts options that have not been explicitly set.", Choices: ["on", "off"]),
        new("Temperature", "Generation", "Temperature", "--temp", "Controls response randomness.", 0),
        new("TopK", "Generation", "Top K", "--top-k", "0 disables the limit.", 0),
        new("TopP", "Generation", "Top P", "--top-p", "Probability threshold.", 0, 1),
        new("MinP", "Generation", "Min P", "--min-p", "Minimum relative probability.", 0, 1),
        new("RepeatPenalty", "Generation", "Repeat penalty", "--repeat-penalty", "1 disables the penalty.", 0),
        new("PresencePenalty", "Generation", "Presence penalty", "--presence-penalty", "Penalty for token presence."),
        new("FrequencyPenalty", "Generation", "Frequency penalty", "--frequency-penalty", "Penalty for token frequency."),
        new("Seed", "Generation", "Seed", "--seed", "−1 uses a random seed.", -1),
        new("Predict", "Generation", "Maximum new tokens", "--predict", "−1 means no explicit limit.", -1),
        new("ChatTemplate", "Chat", "Chat template", "--chat-template", "Leave blank to use the GGUF template. Enter a built-in template name or Jinja template."),
        new("Jinja", "Chat", "Use Jinja", "--jinja", "Leave blank for the server default.", Choices: ["True", "False"], FalseFlag: "--no-jinja"),
        new("Reasoning", "Chat", "Reasoning mode", "--reasoning", "Requires support from the model template.", Choices: ["auto", "on", "off"]),
        new("Parallel", "Advanced", "Parallel requests", "--parallel", "−1 means auto; a positive number sets the slot count.", -1)
    ];
}
