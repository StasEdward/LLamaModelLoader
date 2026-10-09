namespace LLamaModelLoader.Core;

public static class ReasoningOptions
{
    // Import manually configured flags into the new fields without losing custom values.
    // Conflicts and repeated or malformed flags remain visible for normal validation.
    public static void ImportExtraArguments(ModelProfile profile)
    {
        var defaults = new LlamaOptions();
        foreach (var def in OptionCatalog.All.Where(d => d.Property is nameof(LlamaOptions.ReasoningBudget) or nameof(LlamaOptions.ReasoningBudgetMessage)))
        {
            var matches = profile.ExtraArguments.Select((token, index) => (token, index))
                .Where(x => x.token.Split('=', 2)[0] == def.Flag).ToArray();
            if (matches.Length != 1) continue;
            var (token, index) = matches[0];
            var pair = token.Split('=', 2);
            var value = pair.Length == 2 ? pair[1] : profile.ExtraArguments.ElementAtOrDefault(index + 1);
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)) continue;
            var parsed = new LlamaOptions();
            try { def.Set(parsed, value); }
            catch (ArgumentException) { continue; }
            var current = def.PropertyInfo.GetValue(profile.Options);
            var imported = def.PropertyInfo.GetValue(parsed);
            if (current is not null && !Equals(current, def.PropertyInfo.GetValue(defaults)) && !Equals(current, imported)) continue;
            def.PropertyInfo.SetValue(profile.Options, imported);
            profile.ExtraArguments.RemoveRange(index, pair.Length == 2 ? 1 : 2);
        }
    }
}
