using System.Text;
using System.Text.RegularExpressions;

namespace LLamaModelLoader.Infrastructure;

public sealed record ServerCapabilities(string Version, HashSet<string> Flags)
{
    public void Validate(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i].Split('=', 2)[0];
            if (token.StartsWith('-') && !double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out _) && !Flags.Contains(token))
                throw new ArgumentException($"This server build does not support {token}. Reset this option in the profile.");
        }
    }
}

public sealed partial class ServerProbe
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Key, ServerCapabilities Value)? _cache;
    [GeneratedRegex(@"(?<!\w)--?[a-zA-Z][a-zA-Z0-9-]*")]
    private static partial Regex FlagsPattern();

    public async Task<ServerCapabilities> InspectAsync(string path, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException("llama-server.exe was not found. Check the settings.", path);
        await _gate.WaitAsync(token);
        try
        {
            var file = new FileInfo(path);
            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            if (_cache is { } cached && cached.Key == key) return cached.Value;
            var version = await RunAsync(path, "--version", token);
            var help = await RunAsync(path, "--help", token);
            var flags = FlagsPattern().Matches(help).Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
            if (!flags.Contains("--model") || !flags.Contains("--port")) throw new InvalidDataException("Could not recognize llama-server help output.");
            var value = new ServerCapabilities(version.Trim(), flags);
            _cache = (key, value);
            return value;
        }
        finally { _gate.Release(); }
    }

    private static async Task<string> RunAsync(string path, string argument, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var child = OwnedProcess.Start(path, [argument]);
        var output = ReadBoundedAsync(child.Output, timeout.Token);
        var error = ReadBoundedAsync(child.Error, timeout.Token);
        try
        {
            await child.Process.WaitForExitAsync(timeout.Token);
            var text = await output + "\n" + await error;
            if (child.ExitCode != 0) throw new InvalidOperationException($"{argument}: exit code {child.ExitCode}. {text}");
            return text;
        }
        catch { child.Kill(); try { await Task.WhenAll(output, error); } catch { } throw; }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > 1_000_000) throw new InvalidDataException("Server help output exceeds 1 MB.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
