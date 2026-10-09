using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

public sealed class ServerInstallationsWindow : Window
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ServerInstaller _installer;
    private readonly string _selectedPath;
    private readonly ComboBox _releases = new() { HorizontalAlignment = HorizontalAlignment.Stretch },
        _builds = new() { HorizontalAlignment = HorizontalAlignment.Stretch },
        _installed = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "InstalledBuilds" };
    private readonly TextBlock _status = Text("Check GitHub for available builds, or select an installed build below."),
        _download = Text(""), _details = Text(""), _selection = Text("");
    private readonly Button _check = new() { Content = "Check for updates" }, _install = new() { Content = "Install selected build", IsEnabled = false, Classes = { "primary" } },
        _cancel = new() { Content = "Cancel", IsEnabled = false }, _use = new() { Content = "Use selected build", IsEnabled = false, Classes = { "primary" } };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 6 };
    private IReadOnlyList<ServerBuild> _available = [];
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private bool _closing;

    public ServerInstallationsWindow(string dataDirectory, string selectedPath, HttpClient? http = null)
    {
        _selectedPath = selectedPath; _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _installer = new ServerInstaller(System.IO.Path.Combine(dataDirectory, "servers"), _http);
        Title = "Install and update llama.cpp"; Width = 980; Height = 850; MinWidth = 780; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var heading = Text("Install and update llama.cpp"); heading.FontSize = 25; heading.FontWeight = FontWeight.SemiBold;
        _status.Name = "InstallationStatus";
        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = Stack(
            heading, Text("Official Windows x64 builds · CPU, Vulkan, and CUDA"),
            Text("Install builds side by side; keep older versions for rollback."),
            Card(Stack(Text("Download from ggml-org/llama.cpp"), _check, Field("Release (recent releases, including rolling builds)", _releases),
                Field("Backend", _builds), _download,
                Text("CUDA includes matching runtime DLLs; choose a version supported by your NVIDIA driver. Drivers are installed separately."),
                Row(_install, _cancel), _progress, _status)),
            Card(Stack(Text("Installed builds"), _selection, _installed, _use,
                new Expander { Header = "Installation details", Content = _details })),
            Text("Use selected build returns its path to Settings. Save settings to apply it to the next start or restart. To roll back, select an older installed build and save again."),
            Text("Installation folder: " + System.IO.Path.Combine(dataDirectory, "servers"))
        ) } };
        _check.Click += (_, _) => Begin(CheckAsync);
        _install.Click += (_, _) => Begin(InstallAsync);
        _cancel.Click += (_, _) => { _cancellation?.Cancel(); _cancel.IsEnabled = false; _status.Text = "Canceling…"; };
        _releases.SelectionChanged += (_, _) =>
        {
            var backend = (_builds.SelectedItem as ServerBuild)?.Backend;
            var choices = _available.Where(b => b.Tag == _releases.SelectedItem as string).ToArray();
            _builds.ItemsSource = choices; _builds.SelectedItem = choices.FirstOrDefault(b => b.Backend == backend) ?? choices.FirstOrDefault();
        };
        _builds.SelectionChanged += (_, _) => UpdateButtons();
        _installed.SelectionChanged += (_, _) => UpdateButtons();
        _use.Click += (_, _) => { if (_installed.SelectedItem is InstalledServer installation) Close(installation.ExecutablePath); };
        Closing += (_, e) =>
        {
            if (_cancellation is null) return;
            e.Cancel = true; _closing = true; _cancellation.Cancel(); _status.Text = "Canceling before closing…";
        };
        Closed += (_, _) => { if (_ownsHttp) _http.Dispose(); };
        try { ReloadInstalled(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    public async Task CancelAndWaitAsync()
    {
        _cancellation?.Cancel();
        await _operation;
    }

    private void Begin(Func<CancellationToken, Task> action)
    {
        if (_cancellation is not null) return;
        _operation = RunAsync(action);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        _cancellation = new CancellationTokenSource(); UpdateButtons(); _progress.IsIndeterminate = true;
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException) { _status.Text = "Canceled. Existing installations and Settings were preserved."; }
        catch (Exception ex) { _status.Text = "Operation failed: " + ex.Message; }
        finally
        {
            _cancellation.Dispose(); _cancellation = null; _progress.IsIndeterminate = false; UpdateButtons();
            if (_closing) Close();
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        _status.Text = "Checking official GitHub releases…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        _available = await new ServerReleaseClient(_http).GetBuildsAsync(timeout.Token);
        _releases.ItemsSource = _available.Select(b => b.Tag).Distinct().ToArray(); _releases.SelectedIndex = 0;
        if (_available.Count == 0)
        { _status.Text = "No supported Windows x64 builds with SHA-256 checksums were found in the 30 most recent releases."; return; }
        var current = _installer.GetInstalled().FirstOrDefault(i => string.Equals(i.ExecutablePath, _selectedPath, StringComparison.OrdinalIgnoreCase));
        var latest = current is null ? null : _available.FirstOrDefault(b => b.Backend == current.Build.Backend);
        _status.Text = latest is null ? "Choose a release and backend. Rolling / prerelease builds are explicitly labeled." :
            latest.Tag == current!.Build.Tag ? $"Selected installation {latest.Tag} is the latest available {latest.Backend} build." :
            latest.PublishedAt > current!.Build.PublishedAt ? $"Update available for {latest.Backend}: {current.Build.Tag} → {latest.Tag}." :
            "The selected installation is newer than the matching builds in this release list.";
        if (latest is not null)
        {
            _releases.SelectedItem = latest.Tag;
            _builds.SelectedItem = _available.FirstOrDefault(b => b.Tag == latest.Tag && b.Backend == latest.Backend);
        }
    }

    private async Task InstallAsync(CancellationToken token)
    {
        if (_builds.SelectedItem is not ServerBuild build) return;
        var progress = new Progress<InstallationProgress>(p =>
        {
            if (_cancellation is null) return;
            _progress.IsIndeterminate = p.TotalBytes == 0;
            _progress.Value = p.TotalBytes == 0 ? 0 : 100d * p.DownloadedBytes / p.TotalBytes;
            _status.Text = p.Message + (p.TotalBytes > 0 ? $"\n{p.DownloadedBytes / 1048576d:N1} / {p.TotalBytes / 1048576d:N1} MiB" : "");
        });
        var installed = await _installer.InstallAsync(build, progress, token);
        ReloadInstalled(installed.ExecutablePath);
        _status.Text = $"Installed {build.Tag} / {build.Backend}. Use selected build, then save Settings.";
    }

    private void ReloadInstalled(string? select = null)
    {
        var installations = _installer.GetInstalled();
        _installed.ItemsSource = installations;
        _installed.SelectedItem = installations.FirstOrDefault(i => string.Equals(i.ExecutablePath, select ?? _selectedPath, StringComparison.OrdinalIgnoreCase)) ?? installations.FirstOrDefault();
        _selection.Text = "Path currently selected in Settings: " + (string.IsNullOrWhiteSpace(_selectedPath) ? "None" : _selectedPath);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var busy = _cancellation is not null;
        _check.IsEnabled = !busy; _cancel.IsEnabled = busy;
        _releases.IsEnabled = _builds.IsEnabled = _installed.IsEnabled = !busy;
        _use.IsEnabled = !busy && _installed.SelectedItem is InstalledServer;
        var build = _builds.SelectedItem as ServerBuild;
        var exists = build is not null && (_installed.ItemsSource?.Cast<InstalledServer>().Any(i => i.Build.DirectoryName == build.DirectoryName) ?? false);
        _install.IsEnabled = !busy && build is not null && !exists;
        _download.Text = build is null ? "" : $"Download: {build.DownloadBytes / 1048576d:N1} MiB · {build.Assets.Length} archive(s) · SHA-256 verification required" +
            (exists ? "\nAlready installed. Select it below." : "") + "\n" + string.Join("\n", build.Assets.Select(a => a.Name));
        _details.Text = _installed.SelectedItem is InstalledServer installed ? installed.ExecutablePath + "\nInstalled " + installed.InstalledAt.ToLocalTime().ToString("g") + "\n" + installed.Version : "No managed builds installed yet. An existing external server can still be selected in Settings.";
    }

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Stack(params Control[] controls) { var panel = new StackPanel { Spacing = 8 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
    private static StackPanel Row(params Control[] controls) { var panel = Stack(controls); panel.Orientation = Orientation.Horizontal; return panel; }
    private static Control Field(string label, Control control) => Stack(Text(label), control);
    private static Border Card(Control child) => new() { Classes = { "card" }, Child = child };
}
