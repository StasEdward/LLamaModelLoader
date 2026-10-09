using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class ProcessTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LLamaModelLoader-process-" + Guid.NewGuid());
    private SessionLog _log = null!;
    private ServerController _controller = null!;
    private ModelProfile _profile = null!;
    private AppSettings _settings = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var model = Path.Combine(_directory, "Model \u03A9 with spaces.gguf"); File.WriteAllText(model, "GGUF");
        _profile = new() { ModelPath = model, Name = "Test" };
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        _settings = new() { ServerPath = Path.Combine(root, "tests", "FakeLlamaServer", "bin", BuildConfiguration, "net10.0", "FakeLlamaServer.exe"), Port = FreePort(), StartupTimeoutSeconds = 5 };
        _log = new(_directory); _controller = new(new ServerProbe(), _log);
        return Task.CompletedTask;
    }
    private static string BuildConfiguration => AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private async Task WaitFor(ServerState state, int seconds = 12)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (_controller.Status.State != state)
        {
            if (_controller.Status.State == ServerState.Failed && state != ServerState.Failed) throw new Exception(_controller.Status.Message);
            await Task.Delay(50, timeout.Token);
        }
    }
    [Fact]
    public async Task StartReadinessRestartStopAndArgumentsRoundTrip()
    {
        var captured = Path.Combine(_directory, "arguments with spaces.json");
        _profile.Name = "Pareto \u03A9 \"BF16\"";
        _profile.ExtraArguments = ["--test-delay", "250", "--test-stderr", "--test-args-file", captured];
        await _controller.StartAsync(_settings, _profile);
        Assert.Equal(ServerState.Starting, _controller.Status.State);
        await WaitFor(ServerState.Ready);
        var firstPid = _controller.Status.ProcessId;
        await _controller.StartAsync(_settings, _profile); Assert.Equal(firstPid, _controller.Status.ProcessId);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(captured));
        Assert.Contains(_profile.ModelPath, data.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
        Assert.Single(data.RootElement.GetProperty("args").EnumerateArray(), x => x.GetString() == "--metrics");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        async Task<string?> ApiModelName()
        {
            using var models = JsonDocument.Parse(await http.GetStringAsync(_controller.Status.BaseUrl + "/v1/models"));
            return models.RootElement.GetProperty("data")[0].GetProperty("id").GetString();
        }
        Assert.Equal(_profile.Name, await ApiModelName());
        var fingerprint = _controller.Status.ConfigurationFingerprint;
        using var statistics = new StatisticsClient();
        var snapshot = await statistics.ReadAsync(_controller.Status.BaseUrl, CancellationToken.None);
        Assert.Null(snapshot.MetricsError); Assert.Null(snapshot.SlotsError);
        Assert.Equal(42, snapshot.Metric("tokens_predicted_total"));
        Assert.Equal(42, Assert.Single(snapshot.Slots).GeneratedTokens);
        var oldStatus = _controller.Status;
        var resources = await _controller.ReadResourcesAsync(oldStatus, CancellationToken.None);
        Assert.NotNull(resources); Assert.True(resources.WorkingSetBytes > 0); Assert.True(resources.ProcessCount > 0);
        _profile.ExtraArguments.Add("--metrics");
        _profile.Name = "Pareto BF16 renamed";
        Assert.NotEqual(fingerprint, Arguments.Fingerprint(_settings, _profile));
        Assert.Equal("Pareto \u03A9 \"BF16\"", await ApiModelName());
        await _controller.RestartAsync(_settings, _profile); await WaitFor(ServerState.Ready);
        Assert.Equal(_profile.Name, await ApiModelName());
        Assert.Null(await _controller.ReadResourcesAsync(oldStatus, CancellationToken.None));
        using var restarted = JsonDocument.Parse(await File.ReadAllTextAsync(captured));
        Assert.Single(restarted.RootElement.GetProperty("args").EnumerateArray(), x => x.GetString() == "--metrics");
        Assert.NotEqual(firstPid, _controller.Status.ProcessId);
        var lastPid = _controller.Status.ProcessId!.Value;
        await _controller.StopAsync(); Assert.Equal(ServerState.Stopped, _controller.Status.State);
        Assert.True(Exited(lastPid)); Assert.True(Exited(firstPid!.Value));
    }
    [Fact]
    public async Task MemorySurvivesLogTruncationAndClearButResetsForNextSession()
    {
        var captured = Path.Combine(_directory, "memory-args.json");
        _profile.ExtraArguments = ["--test-memory", "--test-stderr", "--test-args-file", captured];
        await _controller.StartAsync(_settings, _profile); await WaitFor(ServerState.Ready);
        Assert.Equal(13292, _controller.Memory.Total.GpuMiB);
        Assert.Equal(137, _controller.Memory.Total.HostMiB);
        Assert.DoesNotContain("model buffer size", _log.Snapshot());
        _log.Clear();
        Assert.Equal(599, _controller.Memory.RecurrentState.GpuMiB);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(captured));
        var arguments = data.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Single(arguments, a => a == "--log-verbosity");
        Assert.Equal("4", arguments[Array.IndexOf(arguments, "--log-verbosity") + 1]);
        _profile.ExtraArguments = [];
        await _controller.RestartAsync(_settings, _profile); await WaitFor(ServerState.Ready);
        Assert.False(_controller.Memory.HasData);
    }

    [Theory]
    [InlineData("--log-verbosity", "2")]
    [InlineData("--verbosity=3", null)]
    [InlineData("--log-disable", null)]
    [InlineData("--verbose", null)]
    public async Task ExplicitLoggingPreferencesArePreserved(string flag, string? value)
    {
        var captured = Path.Combine(_directory, "logging-args.json");
        _profile.ExtraArguments = [flag];
        if (value is not null) _profile.ExtraArguments.Add(value);
        _profile.ExtraArguments.AddRange(["--test-args-file", captured]);
        await _controller.StartAsync(_settings, _profile); await WaitFor(ServerState.Ready);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(captured));
        var arguments = data.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(flag, arguments);
        Assert.Equal(flag == "--log-verbosity" ? 1 : 0, arguments.Count(a => a == "--log-verbosity"));
        if (value is not null) Assert.Equal(value, arguments[Array.IndexOf(arguments, flag) + 1]);
    }

    [Fact]
    public async Task StopDuringLoadingDoesNotBecomeReadyLater()
    {
        _profile.ExtraArguments = ["--test-delay", "30000"];
        await _controller.StartAsync(_settings, _profile);
        var pid = _controller.Status.ProcessId!.Value;
        await _controller.StopAsync(); await Task.Delay(600);
        Assert.Equal(ServerState.Stopped, _controller.Status.State); Assert.True(Exited(pid));
    }
    [Fact]
    public async Task InvalidRestartKeepsRunningModel()
    {
        await _controller.StartAsync(_settings, _profile); await WaitFor(ServerState.Ready);
        var pid = _controller.Status.ProcessId;
        _profile.Options.ContextSize = -99;
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.RestartAsync(_settings, _profile));
        Assert.Equal(ServerState.Ready, _controller.Status.State); Assert.Equal(pid, _controller.Status.ProcessId);
    }
    [Fact]
    public async Task FailedLoadAndTimeoutReleaseProcess()
    {
        _profile.ExtraArguments = ["--test-crash"];
        await _controller.StartAsync(_settings, _profile); await WaitFor(ServerState.Failed);
        Assert.Contains("42", _controller.Status.Message);
        _profile.ExtraArguments = ["--test-no-health"];
        await _controller.StartAsync(_settings, _profile); await WaitFor(ServerState.Failed);
        Assert.Contains("exceeded", _controller.Status.Message); Assert.True(Exited(_controller.Status.ProcessId!.Value));
    }
    [Fact]
    public async Task OccupiedPortDoesNotKillItsOwner()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, _settings.Port)); socket.Listen();
        await Assert.ThrowsAnyAsync<SocketException>(() => _controller.StartAsync(_settings, _profile));
        Assert.True(socket.IsBound);
    }
    [Fact]
    public async Task ClosingJobKillsChildAndProbeReadsRealExecutable()
    {
        var probe = await new ServerProbe().InspectAsync(_settings.ServerPath);
        Assert.Contains("--model", probe.Flags);
        var child = OwnedProcess.Start(_settings.ServerPath, Arguments.Build(_settings, _profile));
        var pid = child.Process.Id; child.Dispose();
        for (var i = 0; i < 30 && !Exited(pid); i++) await Task.Delay(50);
        Assert.True(Exited(pid));
    }
    private static bool Exited(int pid)
    { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
    public async Task DisposeAsync() { await _controller.DisposeAsync(); await _log.DisposeAsync(); Directory.Delete(_directory, true); }
}
