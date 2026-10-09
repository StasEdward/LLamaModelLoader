using System.Text.Json;
using System.Text.Json.Serialization;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed record ConfigurationLoad(Configuration Value, string? Warning = null, bool ReadOnly = false);

public sealed class ConfigurationStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "config.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _readOnly;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public ConfigurationLoad Load()
    {
        if (!File.Exists(FilePath)) return new(new());
        try { return new(Read(FilePath)); }
        catch (NewerConfigurationException ex) { _readOnly = true; return new(new(), ex.Message, true); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Preserve the corrupt original before ever allowing a new save.
            try
            {
                File.Copy(FilePath, FilePath + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                var backup = FilePath + ".bak";
                if (File.Exists(backup))
                {
                    var restored = Read(backup);
                    File.Copy(backup, FilePath, true);
                    return new(restored, "The main configuration is damaged. A backup was restored; the original was preserved.");
                }
                _readOnly = true;
                return new(new(), "The configuration is damaged. The original was preserved. Fix or move config.json and restart the application. " + ex.Message, true);
            }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NewerConfigurationException)
            {
                _readOnly = true;
                return new(new(), "Could not restore the configuration. Saving is disabled: " + recovery.Message, true);
            }
        }
    }

    private static Configuration Read(string path)
    {
        var text = File.ReadAllText(path);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("The configuration must be a JSON object.");
        if (!document.RootElement.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
            throw new JsonException("A valid configuration version is required.");
        if (version is not (1 or 2))
            throw new NewerConfigurationException("Unsupported config.json version. The file will not be overwritten.");
        var value = JsonSerializer.Deserialize<Configuration>(text, Json) ?? throw new JsonException("Empty configuration.");
        if (value.Settings is null || value.Settings.ServerPath is null || value.Settings.ModelsDirectory is null || value.Profiles is null ||
            value.Profiles.Any(p => p is null || p.Name is null || p.ModelPath is null || p.Options is null || p.ExtraArguments is null || p.ExtraArguments.Any(x => x is null)) ||
            value.Profiles.Select(p => p.Id).Distinct().Count() != value.Profiles.Count)
            throw new InvalidDataException("Invalid profiles in the configuration.");
        if (!value.Profiles.Any(p => p.Id == value.SelectedProfileId)) value.SelectedProfileId = null;
        value.SchemaVersion = 2;
        foreach (var profile in value.Profiles) SpeculativeOptions.ImportExtraArguments(profile);
        return value;
    }

    public async Task SaveAsync(Configuration configuration)
    {
        await _gate.WaitAsync();
        var temp = FilePath + ".tmp";
        try
        {
            if (_readOnly) throw new InvalidOperationException("Saving is disabled because the configuration could not be read.");
            configuration = configuration.Clone();
            configuration.SchemaVersion = 2;
            foreach (var profile in configuration.Profiles) SpeculativeOptions.ImportExtraArguments(profile);
            Directory.CreateDirectory(DirectoryPath);
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, Json);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); _gate.Release(); }
    }

    private sealed class NewerConfigurationException(string message) : Exception(message);
}
