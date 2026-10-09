using System.Text.Json;
using System.Text.Json.Serialization;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed record SharedProfile(string Name, string Description, string ModelFile, LlamaOptions Options, List<string> ExtraArguments);
public sealed record ProfileBundle(string Format, int Version, List<SharedProfile> Profiles);

/// <summary>Portable profile settings, separate from machine-specific application configuration.</summary>
public static class ProfileTransfer
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxProfiles = 100;
    private const string FormatName = "LLamaModelLoader.Profiles";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Serialize(IReadOnlyList<ModelProfile> profiles)
    {
        var validated = ValidateProfiles(profiles);
        var bundle = new ProfileBundle(FormatName, 1, validated.Select(p => new SharedProfile(
            p.Name, p.Description, Path.GetFileName(p.ModelPath), p.Options, p.ExtraArguments)).ToList());
        ValidateBundle(bundle);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The profile export exceeds 4 MiB. Export fewer profiles.");
        return bytes;
    }

    public static IReadOnlyList<ModelProfile> Deserialize(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The profile file exceeds 4 MiB.");
        // Accept the UTF-8 BOM written by some JSON editors.
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset));
        CheckDuplicateProperties(document.RootElement);
        var bundle = document.RootElement.Deserialize<ProfileBundle>(Json) ?? throw new InvalidDataException("The profile file is empty.");
        ValidateBundle(bundle);
        var profiles = bundle.Profiles.Select(p => new ModelProfile { Name = p.Name, Description = p.Description,
            ModelPath = p.ModelFile, Options = p.Options, ExtraArguments = p.ExtraArguments }).ToArray();
        return ValidateProfiles(profiles);
    }

    public static List<ModelProfile> PrepareImports(IReadOnlyList<ModelProfile> profiles, IReadOnlyList<ModelProfile> existing)
    {
        var copies = ValidateProfiles(profiles);
        var names = existing.Select(p => p.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = existing.Select(p => p.Id).ToHashSet();
        foreach (var profile in copies)
        {
            var original = profile.Name;
            for (var number = 1; !names.Add(profile.Name); number++)
                profile.Name = original + (number == 1 ? " (imported)" : $" (imported {number})");
            do { profile.Id = Guid.NewGuid(); } while (!ids.Add(profile.Id));
        }
        return copies;
    }

    private static List<ModelProfile> ValidateProfiles(IReadOnlyList<ModelProfile> profiles)
    {
        if (profiles.Count is < 1 or > MaxProfiles) throw new InvalidDataException($"Select between 1 and {MaxProfiles} profiles.");
        if (profiles.Any(p => p is null || p.Name is null || p.Description is null || p.ModelPath is null || p.Options is null ||
            p.ExtraArguments is null || p.ExtraArguments.Any(a => a is null))) throw new InvalidDataException("Invalid profile fields.");
        var copies = new Configuration { Profiles = profiles.ToList() }.Clone().Profiles;
        foreach (var profile in copies)
        {
            profile.Name = profile.Name.Trim();
            SpeculativeOptions.ImportExtraArguments(profile); ReasoningOptions.ImportExtraArguments(profile);
            try { Arguments.Build(new(), profile); }
            catch (ArgumentException ex) { throw new InvalidDataException($"Profile '{profile.Name}': {ex.Message}", ex); }
        }
        return copies;
    }

    public static async Task<IReadOnlyList<ModelProfile>> ReadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length > MaxBytes) throw new InvalidDataException("The profile file exceeds 4 MiB.");
        using var buffer = new MemoryStream(); var chunk = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("The profile file exceeds 4 MiB.");
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Deserialize(buffer.ToArray());
    }

    public static async Task WriteAsync(string path, IReadOnlyList<ModelProfile> profiles, CancellationToken token = default)
    {
        var bytes = Serialize(profiles);
        var target = Path.GetFullPath(path);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await stream.WriteAsync(bytes, token); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateBundle(ProfileBundle bundle)
    {
        if (bundle.Format != FormatName || bundle.Version != 1) throw new InvalidDataException("Unsupported profile format or version. Select a file exported by Llama Model Loader.");
        if (bundle.Profiles is null || bundle.Profiles.Count is < 1 or > MaxProfiles) throw new InvalidDataException($"A profile file must contain 1–{MaxProfiles} profiles.");
        foreach (var profile in bundle.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.ModelFile) || profile.ModelFile.IndexOfAny(['/', '\\', ':']) >= 0 ||
                profile.ModelFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !profile.ModelFile.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Each shared profile must specify a GGUF filename without a directory.");
        }
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name);
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }
}
