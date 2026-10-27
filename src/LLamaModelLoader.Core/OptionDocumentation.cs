namespace LLamaModelLoader.Core;

/// <summary>Editor help for an option. ServerDefault describes what llama-server uses when the flag is omitted;
/// Recommended is the value the editor's reset button enters (null clears the field to keep the server default).</summary>
public sealed record OptionDoc(string Description, string ServerDefault, string? Recommended = null);

// Descriptions and defaults follow the llama-server reference (tools/server/README.md, reviewed October 20, 2026).
// Individual builds can differ; the executable's own --help is authoritative.
public static class OptionDocumentation
{
    public const string Source = "llama.cpp server documentation (tools/server/README.md)";

    private static readonly Dictionary<string, OptionDoc> Docs = new(StringComparer.Ordinal)
    {
        ["ContextSize"] = new("Size of the prompt context in tokens. 0 loads the value from the model. When this is left unset, --fit may reduce the context (down to --fit-ctx, 4096 by default) so the model fits in device memory; an explicit value, including 0, disables that adjustment. A larger context needs more KV cache memory.",
            "0 (from model)"),
        ["GpuLayers"] = new("Maximum number of layers to store in VRAM: an exact number, 'auto', or 'all'. Layers that are not offloaded run on the CPU. When this is left unset, --fit can lower the layer count to fit in device memory.",
            "auto"),
        ["Threads"] = new("Number of CPU threads to use during generation. When unset, llama.cpp chooses automatically. For CPU inference the number of physical cores is usually best; more threads rarely help.",
            "auto"),
        ["ThreadsBatch"] = new("Number of threads to use during batch and prompt processing. Prompt processing parallelizes better than generation, so a value above --threads can help.",
            "same as --threads"),
        ["BatchSize"] = new("Logical maximum batch size: the most prompt tokens submitted to the model in one decode call. Larger values can speed up long prompts but use more compute memory.",
            "2048", "2048"),
        ["MicroBatchSize"] = new("Physical maximum batch size: the most tokens computed at once on the device. Must not exceed the batch size. Larger values speed up prompt processing on GPUs at the cost of a bigger compute buffer.",
            "512", "512"),
        ["SpecType"] = new("Comma-separated list of speculative decoding types: none, draft-simple, draft-eagle3, draft-mtp, draft-dflash, draft-dspark, ngram-simple, ngram-map-k, ngram-map-k4v, ngram-mod, ngram-cache. draft-mtp uses multi-token prediction heads stored in compatible model weights; ngram methods draft from text already in the context and need no extra model.",
            "none", "none"),
        ["SpecDraftNMax"] = new("Number of tokens to draft for speculative decoding. The main model verifies all drafted tokens in one pass; more draft tokens only help when they are usually accepted.",
            "3", "3"),
        ["SpecDraftPMin"] = new("Minimum speculative decoding probability (greedy). Drafting stops when the draft token's probability falls below this threshold; 0 disables the check. Alias: --draft-p-min.",
            "0.00", "0"),
        ["FlashAttention"] = new("Set Flash Attention use: 'on', 'off', or 'auto'. Flash Attention reduces memory use and usually speeds up inference; 'auto' enables it when the model and backend support it. A quantized V cache requires it.",
            "auto", "auto"),
        ["CacheTypeK"] = new("KV cache data type for K. Allowed values: f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1. q8_0 roughly halves KV cache memory with little quality loss; 4-bit types save more but can reduce quality.",
            "f16", "f16"),
        ["CacheTypeV"] = new("KV cache data type for V. Allowed values: f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1. Quantized V cache types require Flash Attention.",
            "f16", "f16"),
        ["LoadMode"] = new("Model loading mode. auto: mmap, unless a device does not support it. none: no special loading mode. mmap: memory-map the model. mlock: force the system to keep the model in RAM rather than swapping or compressing. mmap+mlock: both. dio: use DirectIO if available.",
            "auto", "auto"),
        ["Fit"] = new("Whether to adjust unset arguments to fit in device memory: 'on' or 'off'. With fit on, llama.cpp can lower the context size and GPU layers you leave blank; explicitly set values are not changed. The free margin kept per device is --fit-target (1024 MiB by default).",
            "on", "on"),
        ["Temperature"] = new("Sampling temperature. Lower values make output more focused and deterministic; higher values make it more varied. 0 always picks the most likely token. This is the server default; API requests can override it.",
            "0.80", "0.8"),
        ["TopK"] = new("Top-k sampling: keep only the K most likely tokens. 0 disables it.",
            "40", "40"),
        ["TopP"] = new("Top-p (nucleus) sampling: keep the smallest set of tokens whose cumulative probability reaches P. 1.0 disables it.",
            "0.95", "0.95"),
        ["MinP"] = new("Min-p sampling: drop tokens whose probability is below P times the probability of the most likely token. 0.0 disables it.",
            "0.05", "0.05"),
        ["RepeatPenalty"] = new("Penalize repeated sequences of tokens within the last --repeat-last-n tokens (64 by default). Values above 1 discourage repetition; 1.0 disables it.",
            "1.00", "1"),
        ["PresencePenalty"] = new("Repeat alpha presence penalty: a flat penalty for any token that has already appeared, which encourages new topics. 0.0 disables it.",
            "0.00", "0"),
        ["FrequencyPenalty"] = new("Repeat alpha frequency penalty: a penalty that grows with how often a token has already appeared. 0.0 disables it.",
            "0.00", "0"),
        ["Seed"] = new("RNG seed. -1 uses a random seed; a fixed value makes sampling reproducible for identical requests and settings.",
            "-1 (random)", "-1"),
        ["Predict"] = new("Number of tokens to predict. -1 means infinity: generation continues until an end-of-generation token or a full context. API requests can set their own limit.",
            "-1 (infinity)", "-1"),
        ["ChatTemplate"] = new("Set a custom Jinja chat template. By default the template is taken from the model's metadata. Only commonly used built-in templates are accepted unless Jinja is enabled; examples: chatml, llama3, gemma, mistral-v7, phi4, deepseek3, gpt-oss.",
            "from model metadata"),
        ["Jinja"] = new("Whether to use the Jinja template engine for chat (--jinja / --no-jinja). Jinja is required for tool calling and for most modern model templates.",
            "enabled", "True"),
        ["Reasoning"] = new("Use reasoning (thinking) in the chat: 'on', 'off', or 'auto'. 'auto' detects support from the chat template.",
            "auto", "auto"),
        ["ReasoningBudget"] = new("Token budget for thinking: -1 for unrestricted, 0 for an immediate end, N > 0 for a token budget. When the budget is exhausted, the server ends the thinking block. The application recommends 8192 to keep thinking bounded.",
            "-1 (unrestricted)"),
        ["ReasoningBudgetMessage"] = new("Message injected before the end-of-thinking tag when the reasoning budget is exhausted.",
            "none"),
        ["Parallel"] = new("Number of server slots. -1 means auto. Each slot serves one request at a time and the context is shared among slots, so more slots leave less context per request. The application recommends 1 for a single local user.",
            "-1 (auto)")
    };

    public static OptionDoc For(string property) =>
        Docs.TryGetValue(property, out var doc) ? doc : throw new KeyNotFoundException($"No documentation for option {property}.");
}
