namespace LLamaModelLoader.Core;

public static class SpeculativeOptions
{
    // Preserve profiles created using the previous version's additional-arguments editor.
    // Ambiguous or invalid entries stay visible there and are rejected by normal validation.
    public static void ImportExtraArguments(ModelProfile profile)
    {
        foreach (var def in OptionCatalog.All.Where(d => d.Group == "Speculative decoding"))
        {
            var matches = profile.ExtraArguments.Select((token, index) => (token, index))
                .Where(x => x.token.Split('=', 2)[0] == def.Flag ||
                    def.Flag == "--spec-draft-p-min" && x.token.Split('=', 2)[0] == "--draft-p-min").ToArray();
            if (matches.Length != 1 || def.PropertyInfo.GetValue(profile.Options) is not null) continue;
            var (token, index) = matches[0];
            var pair = token.Split('=', 2);
            var value = pair.Length == 2 ? pair[1] : profile.ExtraArguments.ElementAtOrDefault(index + 1);
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-')) continue;
            var options = new LlamaOptions();
            try { def.Set(options, value); }
            catch (ArgumentException) { continue; }
            def.PropertyInfo.SetValue(profile.Options, def.PropertyInfo.GetValue(options));
            profile.ExtraArguments.RemoveRange(index, pair.Length == 2 ? 1 : 2);
        }
    }
}
