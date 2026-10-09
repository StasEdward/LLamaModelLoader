namespace LLamaModelLoader.Core;

public sealed record GgufMetadata(string? Name, string? Architecture, string? SizeLabel,
    ulong StoredParameters, ulong TensorCount, ulong? ContextLength, ulong? Layers,
    string? Tokenizer, ulong? VocabularySize, string? ChatTemplate,
    IReadOnlyDictionary<string, long> TensorTypes, int Parts, long FileBytes, uint Version);
