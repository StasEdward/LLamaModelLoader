using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLamaModelLoader.Core;

public static class Arguments
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "-m", "--model", "--host", "--port", "--alias", "-a", "-c", "-ngl", "--n-gpu-layers", "-t", "-tb", "-b", "-ub",
        "-fa", "-ctk", "-ctv", "-lm", "-fit", "-s", "-n", "--n-predict", "--temperature", "-np", "-rea",
        "--model-url", "-mu", "--hf", "-hf", "--hf-repo", "-hfr", "--hf-file", "-hff", "--hf-repo-draft", "-hfd",
        "--models-dir", "--models-preset", "--models-max", "--models-autoload", "--no-models-autoload",
        "--api-prefix", "--path", "--ssl-key-file", "--ssl-cert-file", "--api-key", "--api-key-file",
        "--help", "-h", "--version", "--list-devices", "--completion-bash", "--log-file", "--logdir", "--draft-p-min"
    };

    public static List<string> Build(AppSettings settings, ModelProfile profile)
    {
        profile = new Configuration { Profiles = [profile] }.Clone().Profiles[0];
        SpeculativeOptions.ImportExtraArguments(profile);
        if (settings.Port is < 1 or > 65535) throw new ArgumentException("Port: a number from 1 to 65535.");
        if (settings.StartupTimeoutSeconds is < 5 or > 86400) throw new ArgumentException("Loading timeout: from 5 to 86400 seconds.");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Enter a profile name.");
        if (profile.Name.Contains(',') || profile.Name.Any(char.IsControl))
            throw new ArgumentException("The name is used as the API model name: remove commas and control characters.");
        if (string.IsNullOrWhiteSpace(profile.ModelPath)) throw new ArgumentException("Enter the GGUF path.");
        var result = new List<string> { "--model", profile.ModelPath, "--alias", profile.Name.Trim(), "--host", "127.0.0.1", "--port", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        // Jinja is deliberately emitted before a custom template.
        foreach (var def in OptionCatalog.All.OrderBy(x => x.Property == "Jinja" ? 0 : 1))
        {
            var value = def.PropertyInfo.GetValue(profile.Options);
            if (value is null) continue;
            var formatted = def.Format(profile.Options);
            def.Set(new LlamaOptions(), formatted); // Validate persisted data as well as editor input.
            if (value is bool b) result.Add(b ? def.Flag : def.FalseFlag!);
            else { result.Add(def.Flag); result.Add(formatted); }
        }
        if (profile.Options.Parallel == 0) throw new ArgumentException("Parallel requests: −1 or a number ≥ 1.");
        if (profile.Options.BatchSize is { } batch && profile.Options.MicroBatchSize is { } micro && micro > batch)
            throw new ArgumentException("Microbatch size must not exceed batch size.");
        foreach (var token in profile.ExtraArguments)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Contains('\0') || token.Contains('\n'))
                throw new ArgumentException("Additional arguments: each token must be nonempty and occupy one line.");
            var flag = token.Split('=', 2)[0];
            if (Reserved.Contains(flag) || OptionCatalog.All.Any(x => x.Flag == flag || x.FalseFlag == flag))
                throw new ArgumentException($"Argument {flag} is managed by the application or is incompatible with the local server.");
        }
        result.AddRange(profile.ExtraArguments);
        return result;
    }

    public static string Preview(string executable, IReadOnlyList<string> args)
    {
        static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        var masked = new List<string> { Quote(executable) };
        var secret = false;
        foreach (var token in args)
        {
            var sensitive = token.Contains("key", StringComparison.OrdinalIgnoreCase) || token.Contains("token", StringComparison.OrdinalIgnoreCase);
            masked.Add(secret ? "[redacted]" : sensitive && token.Contains('=') ? token.Split('=')[0] + "=[redacted]" : Quote(token));
            secret = !secret && token.StartsWith('-') && sensitive && !token.Contains('=');
        }
        return string.Join(" ", masked);
    }

    public static string Fingerprint(AppSettings settings, ModelProfile profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { settings.ServerPath, settings.Port, profile }))));
}
