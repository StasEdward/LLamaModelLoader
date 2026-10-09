using System.Text.Json;

namespace LLamaModelLoader.Core;

public sealed class Configuration
{
    public int SchemaVersion { get; set; } = 2;
    public AppSettings Settings { get; set; } = new();
    public Guid? SelectedProfileId { get; set; }
    public List<ModelProfile> Profiles { get; set; } = [];
    public Configuration Clone() => JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(this))!;
}

public sealed class AppSettings
{
    public string ServerPath { get; set; } = "";
    public string ModelsDirectory { get; set; } = "";
    public int Port { get; set; } = 8080;
    public int StartupTimeoutSeconds { get; set; } = 300;
    public bool StartWithWindows { get; set; }
    public bool AutoStartModel { get; set; }
    public bool StartMinimized { get; set; }
}

public sealed class ModelProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New model";
    public string Description { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public LlamaOptions Options { get; set; } = new();
    public List<string> ExtraArguments { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class LlamaOptions
{
    public int? ContextSize { get; set; }
    public string? GpuLayers { get; set; }
    public int? Threads { get; set; }
    public int? ThreadsBatch { get; set; }
    public int? BatchSize { get; set; }
    public int? MicroBatchSize { get; set; }
    public string? SpecType { get; set; }
    public int? SpecDraftNMax { get; set; }
    public double? SpecDraftPMin { get; set; }
    public string? FlashAttention { get; set; }
    public string? CacheTypeK { get; set; }
    public string? CacheTypeV { get; set; }
    public string? LoadMode { get; set; }
    public string? Fit { get; set; }
    public double? Temperature { get; set; }
    public int? TopK { get; set; }
    public double? TopP { get; set; }
    public double? MinP { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public int? Seed { get; set; }
    public int? Predict { get; set; }
    public string? ChatTemplate { get; set; }
    public bool? Jinja { get; set; }
    public string? Reasoning { get; set; }
    public int? ReasoningBudget { get; set; } = 8192;
    public string? ReasoningBudgetMessage { get; set; } = "Enough thinking. Act now: make the next tool call.";
    public int? Parallel { get; set; } = 1;
}

public enum ServerState { Stopped, Starting, Ready, Stopping, Failed }

public sealed record ServerStatus(ServerState State, string Message, Guid? ProfileId = null,
    int? ProcessId = null, string? BaseUrl = null, DateTimeOffset? StartedAt = null,
    bool ApiAvailable = false, string? ConfigurationFingerprint = null, bool WebUiEnabled = true);
