using System.Globalization;
using System.Text.Json;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class SpeculativeTests
{
    [Fact]
    public void ExplicitOptionsProduceRequestedArgumentsAndDefaultsDoNotEnableMtp()
    {
        var profile = new ModelProfile { ModelPath = "model.gguf" };
        Assert.DoesNotContain("--spec-type", Arguments.Build(new(), profile));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            profile.Options = new() { GpuLayers = "99", SpecType = "draft-mtp", SpecDraftNMax = 2 };
            OptionCatalog.All.Single(d => d.Property == "SpecDraftPMin").Set(profile.Options, "0,60");
            var args = Arguments.Build(new(), profile);
            Assert.Equal("draft-mtp", args[args.IndexOf("--spec-type") + 1]);
            Assert.Equal("2", args[args.IndexOf("--spec-draft-n-max") + 1]);
            Assert.Equal("0.6", args[args.IndexOf("--spec-draft-p-min") + 1]);
            Assert.Equal("99", args[args.IndexOf("--gpu-layers") + 1]);
            var server = new ServerCapabilities("old", ["--model", "--alias", "--host", "--port", "--gpu-layers", "--parallel"]);
            Assert.Contains("--spec-type", Assert.Throws<ArgumentException>(() => server.Validate(args)).Message);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("SpecDraftNMax", "-1")]
    [InlineData("SpecDraftNMax", "2.5")]
    [InlineData("SpecDraftPMin", "-0.1")]
    [InlineData("SpecDraftPMin", "1.01")]
    [InlineData("SpecDraftPMin", "NaN")]
    public void RejectsInvalidDraftLimits(string property, string value) =>
        Assert.Throws<ArgumentException>(() => OptionCatalog.All.Single(d => d.Property == property).Set(new(), value));

    [Fact]
    public void LegacyArgumentsRemainUsableWithoutMutationOrDuplicateFlags()
    {
        var profile = new ModelProfile { ModelPath = "model.gguf", ExtraArguments = ["--spec-type", "draft-mtp", "--spec-draft-n-max=2", "--draft-p-min", "0.60", "--no-webui"] };
        var args = Arguments.Build(new(), profile);
        Assert.Single(args, a => a == "--spec-type");
        Assert.Equal("0.6", args[args.IndexOf("--spec-draft-p-min") + 1]);
        Assert.Contains("--no-webui", args);
        Assert.Null(profile.Options.SpecType); Assert.Equal(6, profile.ExtraArguments.Count);
        profile.Options.SpecType = "none";
        Assert.Throws<ArgumentException>(() => Arguments.Build(new(), profile));
    }

    [Fact]
    public void InvalidOrRepeatedLegacyFlagsAreNotSilentlyDiscarded()
    {
        foreach (var extras in new List<string>[] { ["--spec-draft-p-min", "2"], ["--spec-type", "draft-mtp", "--spec-type", "none"], ["--spec-type", "--no-webui"] })
        {
            var profile = new ModelProfile { ModelPath = "model.gguf", ExtraArguments = extras };
            var original = extras.ToArray();
            SpeculativeOptions.ImportExtraArguments(profile);
            Assert.Equal(original, profile.ExtraArguments);
            Assert.Throws<ArgumentException>(() => Arguments.Build(new(), profile));
        }
    }

    [Fact]
    public async Task VersionOneConfigurationImportsExtrasAndSavesNewFieldsSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LLamaModelLoader-spec-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ConfigurationStore(directory);
            var profile = new ModelProfile { ModelPath = "model.gguf", ExtraArguments = ["--spec-type", "draft-mtp", "--spec-draft-n-max", "2", "--spec-draft-p-min", "0.60"] };
            var legacy = JsonSerializer.Serialize(new Configuration { SchemaVersion = 1, Profiles = [profile], SelectedProfileId = profile.Id });
            await File.WriteAllTextAsync(store.FilePath, legacy);
            var loaded = store.Load();
            Assert.Null(loaded.Warning); Assert.False(loaded.ReadOnly);
            Assert.Equal(legacy, await File.ReadAllTextAsync(store.FilePath));
            Assert.Equal("draft-mtp", loaded.Value.Profiles[0].Options.SpecType);
            Assert.Empty(loaded.Value.Profiles[0].ExtraArguments);
            await store.SaveAsync(loaded.Value);
            var saved = new ConfigurationStore(directory).Load();
            Assert.Equal(2, saved.Value.SchemaVersion);
            Assert.Equal(2, saved.Value.Profiles[0].Options.SpecDraftNMax);
            Assert.Equal(0.6, saved.Value.Profiles[0].Options.SpecDraftPMin);
            Assert.Equal(legacy, await File.ReadAllTextAsync(store.FilePath + ".bak"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
