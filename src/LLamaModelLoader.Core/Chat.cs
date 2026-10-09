namespace LLamaModelLoader.Core;

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatTarget(string BaseUrl, string Model, int ProcessId, DateTimeOffset StartedAt);
public sealed record ChatUpdate(string Content = "", string Reasoning = "", string? FinishReason = null,
    int? PromptTokens = null, int? CompletionTokens = null);
