using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class ProfileTransferTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "profile-transfer-" + Guid.NewGuid());
    private static ModelProfile Sample() => new() { Name = "Qwen \u03A9", Description = "Shared settings", ModelPath = @"D:\Private\Models\qwen.gguf",
        Options = new() { ContextSize = 32768, GpuLayers = "auto", Threads = 16, ThreadsBatch = 32, Temperature = 0.65,
            CacheTypeK = "q8_0", CacheTypeV = "q4_0", SpecType = "draft-mtp", SpecDraftNMax = 2, SpecDraftPMin = 0.6,
            ReasoningBudget = null, ReasoningBudgetMessage = null, Jinja = false, Parallel = 1 }, ExtraArguments = ["--no-webui"] };
    public ProfileTransferTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task PortableRoundTripPreservesOptionsAndOmitsMachineConfiguration()
    {
        var original = Sample(); var path = Path.Combine(_folder, "profiles.json");
        await ProfileTransfer.WriteAsync(path, [original]);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal("LLamaModelLoader.Profiles", json.RootElement.GetProperty("Format").GetString());
        Assert.False(json.RootElement.TryGetProperty("Settings", out _));
        var exported = json.RootElement.GetProperty("Profiles")[0];
        Assert.False(exported.TryGetProperty("Id", out _)); Assert.False(exported.TryGetProperty("ModelPath", out _));
        Assert.Equal("qwen.gguf", exported.GetProperty("ModelFile").GetString());
        var imported = Assert.Single(await ProfileTransfer.ReadAsync(path));
        Assert.Equal(original.Name, imported.Name); Assert.Equal(original.Description, imported.Description);
        Assert.Equal(JsonSerializer.Serialize(original.Options), JsonSerializer.Serialize(imported.Options));
        Assert.Equal(original.ExtraArguments, imported.ExtraArguments); Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(@"D:\Private\Models\qwen.gguf", original.ModelPath);
    }

    [Fact]
    public void CreatesUniqueNamesAndIdsWithoutChangingExistingProfiles()
    {
        var original = Sample(); var other = Sample(); other.Name = original.Name + " (imported)";
        var imported = ProfileTransfer.PrepareImports([original, original], [original, other]);
        Assert.Equal(original.Name + " (imported 2)", imported[0].Name);
        Assert.Equal(original.Name + " (imported 3)", imported[1].Name);
        Assert.Equal(2, imported.Select(p => p.Id).Distinct().Count());
        Assert.DoesNotContain(imported, p => p.Id == original.Id);
        Assert.Equal("Qwen \u03A9", original.Name);
        imported[0].Options.ContextSize = 512; Assert.Equal(32768, original.Options.ContextSize);
        var exportedTwice = ProfileTransfer.Deserialize(ProfileTransfer.Serialize([original, original]));
        Assert.All(exportedTwice, p => Assert.Equal(original.Name, p.Name));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("Format")]
    [InlineData("Options")]
    [InlineData("ExtraArguments")]
    [InlineData("Name")]
    [InlineData("UnknownOption")]
    [InlineData("InvalidOption")]
    [InlineData("ReservedFlag")]
    [InlineData("Traversal")]
    public void RejectsInvalidBundles(string kind)
    {
        var root = JsonNode.Parse(ProfileTransfer.Serialize([Sample()]))!;
        var profile = root["Profiles"]![0]!;
        switch (kind)
        {
            case "Version": root["Version"] = 2; break;
            case "Format": root["Format"] = "Other app"; break;
            case "Options": profile["Options"] = null; break;
            case "ExtraArguments": profile["ExtraArguments"] = null; break;
            case "Name": profile["Name"] = ""; break;
            case "UnknownOption": profile["Options"]!["Unknown"] = 1; break;
            case "InvalidOption": profile["Options"]!["ContextSize"] = -10; break;
            case "ReservedFlag": profile["ExtraArguments"] = new JsonArray("--port=9999"); break;
            case "Traversal": profile["ModelFile"] = "../model.gguf"; break;
        }
        var error = Record.Exception(() => ProfileTransfer.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.True(error is JsonException or InvalidDataException, error?.ToString() ?? "Invalid import was accepted");
    }

    [Fact]
    public void RejectsDuplicatesOversizedAndEmptyFilesButAcceptsUtf8Bom()
    {
        var bytes = ProfileTransfer.Serialize([Sample()]);
        Assert.Single(ProfileTransfer.Deserialize([0xEF, 0xBB, 0xBF, .. bytes]));
        var duplicate = Encoding.UTF8.GetString(bytes).Replace("\"Version\": 1", "\"Version\": 1, \"Version\": 1");
        Assert.Throws<InvalidDataException>(() => ProfileTransfer.Deserialize(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<InvalidDataException>(() => ProfileTransfer.Deserialize(new byte[ProfileTransfer.MaxBytes + 1]));
        Assert.ThrowsAny<JsonException>(() => ProfileTransfer.Deserialize([]));
        Assert.Throws<InvalidDataException>(() => ProfileTransfer.Serialize([]));
        Assert.Throws<InvalidDataException>(() => ProfileTransfer.Serialize(Enumerable.Range(0, 101).Select(_ => Sample()).ToArray()));
    }

    [Fact]
    public async Task CanceledOrInvalidExportPreservesDestination()
    {
        var path = Path.Combine(_folder, "existing.json"); await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProfileTransfer.WriteAsync(path, [Sample()], cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        var invalid = Sample(); invalid.Options.Parallel = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ProfileTransfer.WriteAsync(path, [invalid]));
        Assert.Equal("original", await File.ReadAllTextAsync(path)); Assert.Single(Directory.GetFiles(_folder));
    }
    public void Dispose() => Directory.Delete(_folder, true);
}
