using System.Globalization;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LLamaModelLoader-tests-" + Guid.NewGuid());
    public CoreTests() => Directory.CreateDirectory(_directory);
    [Fact]
    public void DefaultsAndExplicitZeroRemainDifferentAcrossCultures()
    {
        var profile = new ModelProfile { ModelPath = @"D:\Models\a b.gguf" };
        var args = Arguments.Build(new(), profile);
        Assert.DoesNotContain("--ctx-size", args);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            profile.Options.ContextSize = 0; profile.Options.Temperature = 0.75; profile.Options.GpuLayers = "auto"; profile.Options.Jinja = false;
            args = Arguments.Build(new(), profile);
            Assert.Equal("0", args[args.IndexOf("--ctx-size") + 1]);
            Assert.Equal("0.75", args[args.IndexOf("--temp") + 1]);
            Assert.Contains("--no-jinja", args); Assert.Contains(profile.ModelPath, args);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
    [Theory]
    [InlineData("--port=9999")]
    [InlineData("-m")]
    [InlineData("-ngl")]
    [InlineData("--hf-repo")]
    [InlineData("--models-dir")]
    [InlineData("--api-key")]
    [InlineData("--alias=other")]
    [InlineData("-a")]
    public void CannotOverrideManagedArguments(string flag) =>
        Assert.Throws<ArgumentException>(() => Arguments.Build(new(), new() { ModelPath = "x.gguf", ExtraArguments = [flag] }));
    [Theory]
    [InlineData("a,b")]
    [InlineData("a\nb")]
    public void ModelNameMustRepresentOneApiAlias(string name) =>
        Assert.Throws<ArgumentException>(() => Arguments.Build(new(), new() { Name = name, ModelPath = "x.gguf" }));
    [Fact]
    public async Task SavesBackupsAndRecoversDamagedConfiguration()
    {
        var store = new ConfigurationStore(_directory);
        var first = new ModelProfile { Name = "First", ModelPath = @"D:\Models\x.gguf", Options = new() { GpuLayers = "all", ContextSize = 0, Jinja = false } };
        var config = new Configuration { Profiles = [first], SelectedProfileId = first.Id };
        await store.SaveAsync(config);
        config.Profiles[0].Name = "Second"; await store.SaveAsync(config);
        var saved = new ConfigurationStore(_directory).Load();
        Assert.Equal("Second", saved.Value.Profiles[0].Name);
        Assert.Equal(0, saved.Value.Profiles[0].Options.ContextSize);
        Assert.False(saved.Value.Profiles[0].Options.Jinja);
        await File.WriteAllTextAsync(store.FilePath, "{broken");
        var recovered = new ConfigurationStore(_directory).Load();
        Assert.Equal("First", recovered.Value.Profiles[0].Name); Assert.NotNull(recovered.Warning);
        Assert.Single(Directory.GetFiles(_directory, "*.damaged-*"));
    }
    [Fact]
    public async Task NewerSchemaIsNeverOverwritten()
    {
        var store = new ConfigurationStore(_directory);
        await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":99}");
        Assert.True(store.Load().ReadOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new()));
        Assert.Contains("99", await File.ReadAllTextAsync(store.FilePath));
    }
    [Fact]
    public async Task CatalogGroupsShardsAndRejectsMissingPart()
    {
        var first = Path.Combine(_directory, "model-00001-of-00002.gguf");
        var second = Path.Combine(_directory, "model-00002-of-00002.gguf");
        await File.WriteAllTextAsync(first, "GGUF");
        Assert.NotNull(ModelCatalog.Inspect(first).Error);
        await File.WriteAllTextAsync(second, "GGUF");
        await File.WriteAllTextAsync(Path.Combine(_directory, "mmproj-model.gguf"), "GGUF");
        var catalog = await ModelCatalog.ScanAsync(_directory, default);
        Assert.Single(catalog); Assert.Equal(8, catalog[0].Size); Assert.Null(catalog[0].Error);
        Assert.NotNull(ModelCatalog.Inspect(second).Error);
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
