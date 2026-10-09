using System.Text.Json;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class ReasoningTests
{
    [Fact]
    public void DefaultsAreEmittedAsTwoArgumentsAndLegacyProfilesReceiveThem()
    {
        var profile = JsonSerializer.Deserialize<ModelProfile>("{\"ModelPath\":\"model.gguf\",\"Options\":{}}")!;
        var args = Arguments.Build(new(), profile);
        Assert.Equal("8192", args[args.IndexOf("--reasoning-budget") + 1]);
        Assert.Equal("Enough thinking. Act now: make the next tool call.", args[args.IndexOf("--reasoning-budget-message") + 1]);
        Assert.Single(args, a => a == "--reasoning-budget-message");
        var capabilities = new ServerCapabilities("old", args.Where(a => a.StartsWith("--") && a != "--reasoning-budget").ToHashSet());
        Assert.Contains("--reasoning-budget", Assert.Throws<ArgumentException>(() => capabilities.Validate(args)).Message);
    }

    [Theory]
    [InlineData("-1", -1)]
    [InlineData("0", 0)]
    [InlineData("4096", 4096)]
    public void AcceptsSupportedBudgetValues(string text, int expected)
    {
        var options = new LlamaOptions();
        OptionCatalog.All.Single(d => d.Property == "ReasoningBudget").Set(options, text);
        Assert.Equal(expected, options.ReasoningBudget);
    }

    [Theory]
    [InlineData("-2")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public void RejectsInvalidBudget(string value) =>
        Assert.Throws<ArgumentException>(() => OptionCatalog.All.Single(d => d.Property == "ReasoningBudget").Set(new(), value));

    [Fact]
    public void ClearingBothFieldsSurvivesRoundTripAndOmitsFlags()
    {
        var profile = new ModelProfile { ModelPath = "model.gguf" };
        OptionCatalog.All.Single(d => d.Property == "ReasoningBudget").Set(profile.Options, "");
        OptionCatalog.All.Single(d => d.Property == "ReasoningBudgetMessage").Set(profile.Options, "");
        profile = JsonSerializer.Deserialize<ModelProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Null(profile.Options.ReasoningBudget); Assert.Null(profile.Options.ReasoningBudgetMessage);
        var args = Arguments.Build(new(), profile);
        Assert.DoesNotContain("--reasoning-budget", args); Assert.DoesNotContain("--reasoning-budget-message", args);
    }

    [Fact]
    public async Task ImportsManualValuesAndPersistsWithoutChangingOriginal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "reasoning-test-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            const string message = "Stop thinking. Call \"read_file\" now.";
            var profile = new ModelProfile { ModelPath = "model.gguf", ExtraArguments = ["--reasoning-budget=-1", "--reasoning-budget-message", message] };
            var args = Arguments.Build(new(), profile);
            Assert.Equal("-1", args[args.IndexOf("--reasoning-budget") + 1]);
            Assert.Equal(message, args[args.IndexOf("--reasoning-budget-message") + 1]);
            Assert.Equal(8192, profile.Options.ReasoningBudget); Assert.Equal(3, profile.ExtraArguments.Count);
            var store = new ConfigurationStore(directory);
            await store.SaveAsync(new Configuration { Profiles = [profile] });
            var saved = store.Load().Value.Profiles.Single();
            Assert.Equal(-1, saved.Options.ReasoningBudget); Assert.Equal(message, saved.Options.ReasoningBudgetMessage);
            Assert.Empty(saved.ExtraArguments);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConflictingOrRepeatedArgumentsRemainVisibleAndAreRejected()
    {
        var profile = new ModelProfile { ModelPath = "model.gguf", Options = new() { ReasoningBudget = 100 }, ExtraArguments = ["--reasoning-budget", "200"] };
        Assert.Throws<ArgumentException>(() => Arguments.Build(new(), profile));
        profile.ExtraArguments = ["--reasoning-budget=100", "--reasoning-budget=200"];
        Assert.Throws<ArgumentException>(() => Arguments.Build(new(), profile));
    }
}
