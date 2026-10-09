using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed class ServerController(ServerProbe probe, SessionLog log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private OwnedProcess? _child;
    private CancellationTokenSource? _session;
    private Task _monitor = Task.CompletedTask;
    private Task[] _readers = [];
    private MemoryLogParser _memory = new();
    public MemoryBreakdown Memory => _memory.Snapshot;
    private Configuration? _runningConfiguration;
    public Configuration? RunningConfiguration => _runningConfiguration?.Clone();
    public ServerStatus Status { get; private set; } = new(ServerState.Stopped, "Server stopped");
    public event Action<ServerStatus>? StatusChanged;

    public async Task StartAsync(AppSettings settings, ModelProfile profile, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Status.State is ServerState.Starting or ServerState.Ready or ServerState.Stopping) return;
            await CleanupAsync();
            var arguments = await ValidateAsync(settings, profile, token);
            await StartCoreAsync(settings, profile, arguments);
        }
        catch (Exception ex) { Publish(new(ServerState.Failed, ex.Message)); throw; }
        finally { _gate.Release(); }
    }

    public async Task RestartAsync(AppSettings settings, ModelProfile profile, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            // Complete preflight before disturbing a healthy process.
            var arguments = await ValidateAsync(settings, profile, token);
            Publish(Status with { State = ServerState.Stopping, Message = "Restarting…", ApiAvailable = false });
            await CleanupAsync();
            await StartCoreAsync(settings, profile, arguments);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Publish(Status with { State = ServerState.Stopping, Message = "Stopping…", ApiAvailable = false });
            await CleanupAsync();
            Publish(new(ServerState.Stopped, "Server stopped"));
        }
        finally { _gate.Release(); }
    }

    private async Task<List<string>> ValidateAsync(AppSettings settings, ModelProfile profile, CancellationToken token)
    {
        var args = Arguments.Build(settings, profile);
        var file = ModelCatalog.Inspect(profile.ModelPath);
        if (file.Error is not null) throw new ArgumentException(file.Error);
        var capabilities = await probe.InspectAsync(settings.ServerPath, token);
        capabilities.Validate(args);
        if (capabilities.Flags.Contains("--metrics") && !args.Any(a => a.Split('=', 2)[0] == "--metrics")) args.Add("--metrics");
        // Native llama allocation messages use the trace threshold in recent builds.
        // Preserve an explicit logging preference, even when it hides memory details.
        if (capabilities.Flags.Contains("--log-verbosity") && !args.Any(a => a.Split('=', 2)[0] is
            "--log-verbosity" or "--verbosity" or "-lv" or "--verbose" or "--log-verbose" or "-v" or "--log-disable"))
            args.AddRange(["--log-verbosity", "4"]);
        log.Add(capabilities.Version);
        return args;
    }

    private async Task StartCoreAsync(AppSettings settings, ModelProfile profile, List<string> args)
    {
        try
        {
            await CheckPortAsync(settings.Port);
            var memory = new MemoryLogParser();
            _memory = memory;
            _session = new CancellationTokenSource();
            _child = OwnedProcess.Start(settings.ServerPath, args);
            var child = _child;
            var token = _session.Token;
            var url = $"http://127.0.0.1:{settings.Port}";
            _runningConfiguration = new Configuration { Settings = settings, Profiles = [profile], SelectedProfileId = profile.Id }.Clone();
            Publish(new(ServerState.Starting, "Loading model…", profile.Id, child.Process.Id, url, DateTimeOffset.Now,
                ConfigurationFingerprint: Arguments.Fingerprint(settings, profile), WebUiEnabled: !args.Contains("--no-webui")));
            log.Add(Arguments.Preview(settings.ServerPath, args));
            _readers = [PumpAsync(child.Output, memory, token), PumpAsync(child.Error, memory, token)];
            _monitor = MonitorAsync(child, url, profile.ModelPath, settings.StartupTimeoutSeconds, token);
        }
        catch (Exception ex) { Publish(new(ServerState.Failed, "Startup failed: " + ex.Message)); throw; }
    }

    private static async Task CheckPortAsync(int port)
    {
        // Probe an actual listener: an exclusive bind also rejects closing connections after restart,
        // and a cached endpoint snapshot can outlive the server. Ownership is rechecked at readiness.
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return; }
        catch (OperationCanceledException) { return; } // Inconclusive preflight; launch and ownership checks decide.
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private async Task MonitorAsync(OwnedProcess child, string url, string modelPath, int timeoutSeconds, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var ready = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (child.Process.HasExited)
                    throw new InvalidOperationException($"The server exited with code {child.ExitCode}. See the log for details.");
                var healthy = false;
                try
                {
                    using var response = await _http.GetAsync(url + "/health", token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                        healthy = body.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok";
                        if (healthy && !ready)
                        {
                            using var propertiesResponse = await _http.GetAsync(url + "/props", token);
                            propertiesResponse.EnsureSuccessStatusCode();
                            using var properties = await JsonDocument.ParseAsync(await propertiesResponse.Content.ReadAsStreamAsync(token), cancellationToken: token);
                            if (properties.RootElement.TryGetProperty("model_path", out var path) &&
                                !string.Equals(Path.GetFullPath(path.GetString() ?? ""), Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("A different model is responding on this port.");
                            if (!TcpPortOwner.IsOwnedByJob(new Uri(url).Port, child))
                                throw new InvalidOperationException("The port belongs to another process.");
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !token.IsCancellationRequested) { }
                token.ThrowIfCancellationRequested();
                if (child.Process.HasExited) continue;
                if (healthy)
                {
                    if (!ready || !Status.ApiAvailable) Publish(Status with { State = ServerState.Ready, Message = "Model ready", ApiAvailable = true });
                    ready = true;
                }
                else if (ready && Status.ApiAvailable) Publish(Status with { Message = "The API is not responding. The process is still running.", ApiAvailable = false });
                else if (!ready && DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Loading exceeded {timeoutSeconds} seconds. Increase the timeout in Settings.");
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.Add(ex.Message);
            child.Kill();
            await child.WaitForExitAsync();
            if (!token.IsCancellationRequested) Publish(Status with { State = ServerState.Failed, Message = ex.Message, ApiAvailable = false });
        }
    }

    private async Task PumpAsync(StreamReader reader, MemoryLogParser memory, CancellationToken token)
    {
        void Record(string line) { memory.Add(line); log.Add(line); }
        try
        {
            // Bound even a single unbroken native log line.
            var buffer = new char[2048];
            var pending = new System.Text.StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n' || pending.Length >= 4000) { Record(pending.ToString().TrimEnd('\r')); pending.Clear(); }
                    if (buffer[i] != '\n') pending.Append(buffer[i]);
                }
            if (pending.Length > 0) Record(pending.ToString());
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task CleanupAsync()
    {
        var previousPort = _child is not null && Status.BaseUrl is { } url ? new Uri(url).Port : (int?)null;
        _session?.Cancel();
        if (_child is not null)
        {
            _child.Kill();
            await _child.WaitForExitAsync();
        }
        await _monitor;
        await Task.WhenAll(_readers);
        _child?.Dispose(); _child = null;
        _session?.Dispose(); _session = null;
        _monitor = Task.CompletedTask; _readers = [];
        if (previousPort is { } port)
        {
            // Termination notification can precede TCP listener teardown on Windows.
            // Wait for release before reporting Stopped or launching the replacement.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try { await CheckPortAsync(port); break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && DateTime.UtcNow < deadline)
                { await Task.Delay(50); }
            }
        }
    }
    private void Publish(ServerStatus status) { Status = status; StatusChanged?.Invoke(status); }
    public async Task<ProcessResources?> ReadResourcesAsync(ServerStatus expected, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return null;
        try
        {
            if (_child is null || Status.StartedAt != expected.StartedAt || Status.ProcessId != expected.ProcessId ||
                Status.State is not (ServerState.Starting or ServerState.Ready)) return null;
            return _child.ReadResources();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _http.Dispose(); _gate.Dispose(); }
}
