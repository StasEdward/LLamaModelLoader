using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ConfigurationStore _store;
    private readonly ServerProbe _probe = new();
    private readonly ServerController _server;
    private readonly SessionLog _log;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public Configuration Configuration { get; private set; }
    public bool IsReadOnly { get; }
    public bool HadConfiguration { get; }
    public ObservableCollection<ModelProfile> Profiles { get; } = [];
    public ModelProfile? SelectedProfile => Profiles.FirstOrDefault(p => p.Id == Configuration.SelectedProfileId);
    public string DataDirectory => _store.DirectoryPath;
    public ServerStatus Status => _server.Status;
    public bool ShowMemory => Status.State == ServerState.Ready;
    [ObservableProperty] private MemoryBreakdown _memory = MemoryBreakdown.Empty;
    public Task<ProcessResources?> ReadResourcesAsync(ServerStatus expected, CancellationToken token) => _server.ReadResourcesAsync(expected, token);
    public bool CanChoose => !IsBusy && Status.State is ServerState.Stopped or ServerState.Failed;
    public bool CanStart => CanChoose && SelectedProfile is not null && !IsReadOnly;
    public bool CanStop => Status.State is ServerState.Starting or ServerState.Ready;
    public bool CanRestart => !IsBusy && Status.State == ServerState.Ready;
    public bool CanOpenApi => Status.ApiAvailable;
    public bool CanOpenWebUi => Status.ApiAvailable && Status.WebUiEnabled;
    public string StatusText => Status.Message;
    public string ApiAddress => Status.BaseUrl is { } address ? address + "/v1" : "http://127.0.0.1:" + Configuration.Settings.Port + "/v1";
    public string ModelTitle => SelectedProfile?.Name ?? "Select a model";
    public string ModelDescription => SelectedProfile?.Description is { Length: > 0 } description ? description : "Local models · llama.cpp";
    public string ModelInfo
    {
        get
        {
            if (SelectedProfile is not { } profile) return "Add a GGUF model on the Models page to get started.";
            var file = ModelCatalog.Inspect(profile.ModelPath);
            return file.Summary + "\nContext: " + (profile.Options.ContextSize?.ToString() ?? "default") +
                "   ·   GPU layers: " + (profile.Options.GpuLayers ?? "default");
        }
    }
    public bool NeedsRestart => Status.ConfigurationFingerprint is { } current && SelectedProfile is { } p &&
        Status.State is ServerState.Ready or ServerState.Starting && current != Arguments.Fingerprint(Configuration.Settings, p);
    public bool IsLoading => Status.State is ServerState.Starting or ServerState.Stopping;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _elapsed = "";
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand RestartCommand { get; }

    public MainViewModel(string directory)
    {
        _store = new(directory);
        HadConfiguration = File.Exists(_store.FilePath);
        var loaded = _store.Load();
        Configuration = loaded.Value; IsReadOnly = loaded.ReadOnly; Notice = loaded.Warning ?? "";
        foreach (var profile in Configuration.Profiles) Profiles.Add(profile);
        _log = new(Path.Combine(directory, "logs"));
        _server = new(_probe, _log);
        _server.StatusChanged += _ => Dispatcher.UIThread.Post(Refresh);
        StartCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (SelectedProfile is { } p) await _server.StartAsync(Configuration.Settings, p);
        }), () => CanStart);
        StopCommand = new AsyncRelayCommand(() => RunAsync(_server.StopAsync), () => CanStop);
        RestartCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (SelectedProfile is { } p) await _server.RestartAsync(Configuration.Settings, p);
        }), () => CanRestart);
    }

    public void Tick()
    {
        Memory = ShowMemory ? _server.Memory : MemoryBreakdown.Empty;
        LogText = _log.Snapshot();
        Elapsed = Status.StartedAt is { } start && Status.State is ServerState.Ready or ServerState.Starting
            ? $"Session time {(DateTimeOffset.Now - start):hh\\:mm\\:ss} · PID {Status.ProcessId}" : "";
        if (_log.WriteError is { } error) Notice = "Could not write the log: " + error;
    }
    public async Task RunAsync(Func<Task> operation)
    {
        try { IsBusy = true; Refresh(); Notice = ""; await operation(); }
        catch (OperationCanceledException) { Notice = "Operation canceled."; }
        catch (Exception ex) { Notice = ex.Message; _log.Add(ex.Message); }
        finally { IsBusy = false; Refresh(); }
    }

    public async Task SelectAsync(Guid id)
    {
        if (Status.State is not (ServerState.Stopped or ServerState.Failed) || id == Configuration.SelectedProfileId) return;
        await ChangeAsync(c => c.SelectedProfileId = id);
    }
    public async Task SaveProfileAsync(ModelProfile profile)
    {
        Arguments.Build(Configuration.Settings, profile);
        var file = ModelCatalog.Inspect(profile.ModelPath);
        if (file.Error is not null) throw new ArgumentException(file.Error);
        await ChangeAsync(c =>
        {
            var index = c.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index >= 0) c.Profiles[index] = profile; else c.Profiles.Add(profile);
            c.SelectedProfileId ??= profile.Id;
        });
    }
    public async Task DeleteAsync(Guid id)
    {
        if (Status.ProfileId == id && Status.State is ServerState.Starting or ServerState.Ready or ServerState.Stopping)
            throw new InvalidOperationException("Stop the active model first.");
        await ChangeAsync(c => { c.Profiles.RemoveAll(p => p.Id == id); if (c.SelectedProfileId == id) c.SelectedProfileId = null; });
    }
    public Task DuplicateAsync(ModelProfile profile)
    {
        var copy = new Configuration { Profiles = [profile] }.Clone().Profiles[0];
        copy.Id = Guid.NewGuid(); copy.Name += " — copy";
        return SaveProfileAsync(copy);
    }
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        if (settings.Port is < 1 or > 65535 || settings.StartupTimeoutSeconds is < 5 or > 86400)
            throw new ArgumentException("Check the port (1–65535) and timeout (5–86400 seconds).");
        if (!Path.IsPathFullyQualified(settings.ServerPath) || !File.Exists(settings.ServerPath)) throw new FileNotFoundException("Select an existing llama-server.exe.");
        if (!Directory.Exists(settings.ModelsDirectory)) throw new DirectoryNotFoundException("The models folder was not found.");
        var previous = Configuration.Settings.StartWithWindows;
        if (previous != settings.StartWithWindows) WindowsStartup.SetEnabled(settings.StartWithWindows);
        try { await ChangeAsync(c => c.Settings = settings); }
        catch
        {
            if (previous != settings.StartWithWindows) WindowsStartup.SetEnabled(previous);
            throw;
        }
        Notice = "Settings saved.";
    }
    public Task<ServerCapabilities> ProbeAsync(string path) => _probe.InspectAsync(path);
    public Task<IReadOnlyList<ModelFile>> ScanAsync(CancellationToken token) => ModelCatalog.ScanAsync(Configuration.Settings.ModelsDirectory, token);
    public void ClearLog() { _log.Clear(); Tick(); }
    private async Task ChangeAsync(Action<Configuration> update)
    {
        await _saveGate.WaitAsync();
        try
        {
            var candidate = Configuration.Clone(); update(candidate);
            await _store.SaveAsync(candidate); Configuration = candidate;
            Profiles.Clear(); foreach (var profile in candidate.Profiles) Profiles.Add(profile);
            Refresh();
        }
        finally { _saveGate.Release(); }
    }
    private void Refresh()
    {
        OnPropertyChanged(nameof(ShowMemory));
        Memory = ShowMemory ? _server.Memory : MemoryBreakdown.Empty;
        foreach (var property in new[] { nameof(SelectedProfile), nameof(CanChoose), nameof(CanStart), nameof(CanStop), nameof(CanRestart), nameof(CanOpenApi), nameof(CanOpenWebUi), nameof(StatusText), nameof(ApiAddress), nameof(ModelTitle), nameof(ModelDescription), nameof(ModelInfo), nameof(NeedsRestart), nameof(IsLoading) }) OnPropertyChanged(property);
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync() { await _server.DisposeAsync(); await _log.DisposeAsync(); _saveGate.Dispose(); }
}
