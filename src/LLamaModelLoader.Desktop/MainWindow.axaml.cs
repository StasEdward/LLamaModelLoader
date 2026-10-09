using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _scan;
    private bool _dirty;
    private StatisticsWindow? _statisticsWindow;
    private ChatView? _chat;
    public MainWindow() : this(new MainViewModel(Program.DataDirectory)) { }
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent(); _vm = vm; DataContext = vm;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => vm.Tick());
        _timer.Start();
        Closed += async (_, _) => { _timer.Stop(); _scan?.Cancel(); _statisticsWindow?.Close(); if (_chat is not null) await _chat.DisposeAsync(); };
        Home();
    }

    private static TextBlock Text(string text, string? style = null)
    {
        var control = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style is not null) control.Classes.Add(style);
        return control;
    }
    private static TextBlock BoundText(string property, string? style = null)
    { var control = Text("", style); control.Bind(TextBlock.TextProperty, new Binding(property)); return control; }
    private static StackPanel Stack(params Control[] controls)
    { var stack = new StackPanel { Spacing = 14 }; foreach (var c in controls) stack.Children.Add(c); return stack; }
    private static StackPanel Row(params Control[] controls)
    { var stack = Stack(controls); stack.Orientation = Orientation.Horizontal; stack.Spacing = 10; return stack; }
    private static Border Card(Control content) => new() { Classes = { "card" }, Child = content };
    private static ScrollViewer PageScroll(Control content)
    {
        content.Margin = new Thickness(0, 0, 12, 0);
        return new ScrollViewer { AllowAutoHide = false, Content = content };
    }
    private static Button Button(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary) button.Classes.Add("primary");
        button.Click += async (_, _) => await action();
        return button;
    }
    private static TextBox Input(string value = "", string? placeholder = null) => new() { Text = value, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static Control Field(string label, Control input, string? hint = null) => Stack(Text(label), input, Text(hint ?? "", "muted"));
    private void SetPage(Control content)
    { _scan?.Cancel(); _scan?.Dispose(); _scan = null; PageHost.Content = content; }
    private async Task NavigateAsync(Action action)
    {
        if (_dirty && !await ConfirmAsync("Unsaved changes", "Leave the editor and discard changes?")) return;
        _dirty = false; action();
    }
    private async void HomeClick(object? sender, RoutedEventArgs e) => await NavigateAsync(Home);
    private async void ModelsClick(object? sender, RoutedEventArgs e) => await NavigateAsync(Models);
    private async void ChatClick(object? sender, RoutedEventArgs e) => await NavigateAsync(() => SetPage(_chat ??= new ChatView(_vm)));
    public Task CancelChatAsync() => _chat?.CancelAndWaitAsync() ?? Task.CompletedTask;
    private async void SettingsClick(object? sender, RoutedEventArgs e) => await NavigateAsync(Settings);
    private void StatisticsClick(object? sender, RoutedEventArgs e) => ShowStatistics();
    public void ShowStatistics()
    {
        if (_statisticsWindow is null)
        {
            _statisticsWindow = new StatisticsWindow(_vm);
            _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
            _statisticsWindow.Show();
        }
        _statisticsWindow.Activate();
    }
    private async void ExitClick(object? sender, RoutedEventArgs e)
    {
        if (_dirty && !await ConfirmAsync("Unsaved changes", "Exit the application without saving changes?")) return;
        if (Application.Current is App app) await app.ExitAsync();
    }
    private void Home()
    {
        var chooser = new ComboBox { ItemsSource = _vm.Profiles, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select a profile" };
        chooser.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedProfile)) { Mode = BindingMode.OneWay });
        chooser.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanChoose)));
        chooser.SelectionChanged += async (_, _) =>
        {
            if (!_vm.IsBusy && chooser.SelectedItem is ModelProfile profile && profile.Id != _vm.SelectedProfile?.Id)
                await _vm.RunAsync(() => _vm.SelectAsync(profile.Id));
        };
        var start = new Button { Content = "▶  Start", Command = _vm.StartCommand, Classes = { "primary" } };
        var stop = new Button { Content = "■  Stop", Command = _vm.StopCommand };
        var restart = new Button { Content = "↻  Restart", Command = _vm.RestartCommand };
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 };
        progress.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsLoading)));
        var changed = Text("Settings changed. Restart the server to apply them.", "muted");
        changed.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.NeedsRestart)));
        var copy = Button("Copy API address", () => _vm.RunAsync(async () => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_vm.ApiAddress); }));
        copy.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanOpenApi)));
        var web = Button("Open Web UI ↗", () => _vm.RunAsync(() => { Open(_vm.Status.BaseUrl!); return Task.CompletedTask; }));
        web.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanOpenWebUi)));
        var edit = Button("Edit profile", () => { if (_vm.SelectedProfile is { } profile) Editor(profile); return Task.CompletedTask; });
        var optimize = Button("Optimize…", async () => { if (_vm.SelectedProfile is { } profile) await OptimizeAsync(profile); });
        var metadata = new GgufMetadataView();
        metadata.Bind(GgufMetadataView.ModelPathProperty, new Binding(nameof(MainViewModel.SelectedModelPath)));
        var log = new TextBox { Name = "ServerLogText", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, MinHeight = 160, MaxHeight = 280 };
        log.Bind(TextBox.TextProperty, new Binding(nameof(MainViewModel.LogText)));
        var logs = new Expander { Header = "Server log", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Stack(log,
            Row(Button("Copy", () => _vm.RunAsync(async () => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_vm.LogText); })),
                Button("Clear", () => { _vm.ClearLog(); return Task.CompletedTask; }),
                Button("Log folder", () => _vm.RunAsync(() => { var folder = Path.Combine(_vm.DataDirectory, "logs"); Directory.CreateDirectory(folder); Open(folder); return Task.CompletedTask; })))) };
        var logScrollPending = false;
        void FollowLog()
        {
            if (logScrollPending) return;
            logScrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                logScrollPending = false;
                if (!logs.IsExpanded || TopLevel.GetTopLevel(log) is null) return;
                var scroll = log.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroll is not null) scroll.Offset = new Vector(scroll.Offset.X, scroll.Extent.Height);
            }, DispatcherPriority.Loaded);
        }
        log.TextChanged += (_, _) => FollowLog();
        logs.Expanded += (_, _) => FollowLog();
        var memory = new MemorySummaryView();
        memory.Bind(MemorySummaryView.SnapshotProperty, new Binding(nameof(MainViewModel.Memory)));
        var memoryCard = Card(memory);
        memoryCard.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.ShowMemory)));
        SetPage(PageScroll(Stack(Text("Model control", "title"), Text("Your local server. One profile per session.", "muted"), chooser,
            Card(Stack(BoundText(nameof(MainViewModel.ModelTitle), "title"), BoundText(nameof(MainViewModel.ModelDescription), "muted"), BoundText(nameof(MainViewModel.ModelInfo), "muted"),
                progress, Row(start, stop, restart), changed, Row(edit, optimize))),
            new Expander { Header = "GGUF metadata", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Card(metadata) }, memoryCard,
            Card(Stack(Text("OpenAI-compatible API", "muted"), BoundText(nameof(MainViewModel.ApiAddress)), Row(copy, web))), logs)));
    }

    private void Models()
    {
        var search = Input(placeholder: "Search by name or path…");
        var profiles = new StackPanel { Spacing = 12 };
        void Populate()
        {
            profiles.Children.Clear();
            foreach (var profile in _vm.Profiles.Where(p => (p.Name + " " + p.ModelPath).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)))
            {
                var selected = _vm.SelectedProfile?.Id == profile.Id;
                var active = _vm.Status.ProfileId == profile.Id && _vm.Status.State is ServerState.Ready or ServerState.Starting;
                var choose = Button(selected ? "Selected" : "Select", () => _vm.RunAsync(async () => { await _vm.SelectAsync(profile.Id); Populate(); }));
                choose.IsEnabled = _vm.CanChoose && !selected;
                var delete = Button("Delete", async () =>
                {
                    if (await ConfirmAsync("Delete profile?", $"The profile '{profile.Name}' will be deleted. The GGUF file will remain on disk."))
                        await _vm.RunAsync(async () => { await _vm.DeleteAsync(profile.Id); Populate(); });
                });
                delete.IsEnabled = !active;
                profiles.Children.Add(Card(Stack(Text(profile.Name + (active ? "  • running" : selected ? "  • selected" : "")),
                    Text(profile.ModelPath, "muted"), Text(ModelCatalog.Inspect(profile.ModelPath).Summary, "muted"),
                    Row(choose, Button("Edit", () => { Editor(profile); return Task.CompletedTask; }),
                        Button("Duplicate", () => _vm.RunAsync(async () => { await _vm.DuplicateAsync(profile); Populate(); })), delete,
                        Button("Export…", () => _vm.RunAsync(() => ExportProfilesAsync([profile])))))));
            }
            if (profiles.Children.Count == 0) profiles.Children.Add(Card(Text("No profiles yet. Add a model from the catalog or select a GGUF file.", "muted")));
        }
        search.TextChanged += (_, _) => Populate(); Populate();
        var import = Button("Import…", () => _vm.RunAsync(async () =>
        {
            var path = await PickFileAsync("Import model profiles", ["*.json"]);
            if (path is null) return;
            var imported = await ProfileTransfer.ReadAsync(path);
            await new ProfileImportWindow(_vm, imported).ShowDialog<int>(this);
            Populate();
        }));
        import.IsEnabled = !_vm.IsReadOnly && !_vm.IsOptimizing;
        var exportAll = Button("Export all…", () => _vm.RunAsync(() => ExportProfilesAsync(_vm.Profiles.ToArray())));
        SetPage(PageScroll(Stack(Text("Models", "title"), Text("Launch profiles. Multiple profiles can use the same file.", "muted"),
            Row(Button("＋ Add model", () => { Editor(new()); return Task.CompletedTask; }, true), import, exportAll), search, profiles)));
    }

    private async Task ExportProfilesAsync(IReadOnlyList<ModelProfile> profiles)
    {
        // Validate before opening the save picker, so invalid exports cannot replace an existing file.
        _ = ProfileTransfer.Serialize(profiles);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export model profiles",
            SuggestedFileName = profiles.Count == 1 ? "llama-profile.json" : "llama-profiles.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Profile JSON") { Patterns = ["*.json"] }], ShowOverwritePrompt = true });
        if (file is null) return;
        var path = file.TryGetLocalPath() ?? throw new IOException("Choose a local file for export.");
        var config = Path.GetFullPath(Path.Combine(_vm.DataDirectory, "config.json"));
        if (Path.GetFullPath(path).Equals(config, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(path).Equals(config + ".bak", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different filename; the application configuration and its backup cannot be replaced by an export.");
        await ProfileTransfer.WriteAsync(path, profiles);
        _vm.Notice = $"Exported {profiles.Count} profile(s) to {path}. Model weights are not included.";
    }

    private void Editor(ModelProfile source)
    {
        var editingReady = false;
        var profile = new Configuration { Profiles = [source] }.Clone().Profiles[0];
        SpeculativeOptions.ImportExtraArguments(profile);
        ReasoningOptions.ImportExtraArguments(profile);
        var name = Input(profile.Name); var description = Input(profile.Description); var path = Input(profile.ModelPath, "Absolute GGUF path");
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180, FontSize = 11 };
        var validation = Text("", "muted");
        var extra = new TextBox { Text = string.Join('\n', profile.ExtraArguments), AcceptsReturn = true, MinHeight = 90, PlaceholderText = "One token per line: flag, then value" };
        var fields = new Dictionary<OptionDefinition, Func<string?>>();
        ModelProfile Read()
        {
            var draft = new ModelProfile { Id = profile.Id, Name = name.Text?.Trim() ?? "", Description = description.Text ?? "", ModelPath = path.Text?.Trim() ?? "", Options = new LlamaOptions() };
            foreach (var (def, read) in fields) def.Set(draft.Options, read());
            draft.ExtraArguments = (extra.Text ?? "").Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
            return draft;
        }
        void Changed()
        {
            if (editingReady && path.IsAttachedToVisualTree()) _dirty = true;
            try { var draft = Read(); preview.Text = Arguments.Preview(_vm.Configuration.Settings.ServerPath, Arguments.Build(_vm.Configuration.Settings, draft)); validation.Text = "An empty field uses the server default. Executable capabilities are checked at startup."; }
            catch (Exception ex) { validation.Text = ex.Message; preview.Text = "Correct the options to see a preview."; }
        }
        var browse = Button("Browse GGUF…", () => _vm.RunAsync(async () =>
        {
            var chosen = await PickFileAsync("Select a model", ["*.gguf"]);
            if (chosen is not null) { path.Text = chosen; if (name.Text == "New model") name.Text = Path.GetFileNameWithoutExtension(chosen); }
        }));
        var catalog = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Models in the configured folder" };
        catalog.SelectionChanged += (_, _) => { if (catalog.SelectedItem is ModelFile file) { path.Text = file.Path; if (name.Text == "New model") name.Text = Path.GetFileNameWithoutExtension(file.Path); } };
        var scan = Button("Refresh catalog", () => _vm.RunAsync(async () =>
        {
            _scan?.Cancel(); _scan?.Dispose(); _scan = new CancellationTokenSource();
            catalog.ItemsSource = await _vm.ScanAsync(_scan.Token);
        }));
        var sections = Stack(Text("Profile settings", "title"), Text("Saving does not change the running server's settings.", "muted"),
            Card(Stack(Field("Name", name, "Model name in the API (/v1/models). Passed as --alias; no commas. Changes apply after a restart."), Field("Description", description), Field("Model file", path), Row(browse, scan), catalog)));
        var metadata = new GgufMetadataView();
        metadata.Bind(GgufMetadataView.ModelPathProperty, new Binding(nameof(TextBox.Text)) { Source = path });
        sections.Children.Add(new Expander { Header = "GGUF metadata", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Card(metadata) });
        foreach (var group in OptionCatalog.All.GroupBy(x => x.Group))
        {
            var panel = new StackPanel { Spacing = 18 };
            foreach (var def in group)
            {
                Control control;
                if (def.Choices is not null)
                {
                    var current = def.Format(profile.Options);
                    var values = new[] { "Server default" }.Concat(def.Choices).Concat(current.Length > 0 && !def.Choices.Contains(current) ? [current] : Array.Empty<string>()).ToArray();
                    var combo = new ComboBox { ItemsSource = values, SelectedIndex = Math.Max(0, Array.IndexOf(values, def.Format(profile.Options))), HorizontalAlignment = HorizontalAlignment.Stretch };
                    fields[def] = () => combo.SelectedIndex <= 0 ? null : combo.SelectedItem?.ToString();
                    combo.SelectionChanged += (_, _) => Changed(); control = combo;
                }
                else
                {
                    var input = Input(def.Format(profile.Options), "Server default");
                    fields[def] = () => input.Text; input.TextChanged += (_, _) => Changed(); control = input;
                }
                control.Name = def.Property;
                panel.Children.Add(Field(def.Label, control, def.Hint + "  " + def.Flag));
            }
            sections.Children.Add(new Expander { Header = group.Key, IsExpanded = group.Key == "General", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Card(panel) });
        }
        sections.Children.Add(new Expander { Header = "Additional arguments", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Field("CLI argument tokens", extra, "Do not quote paths. Put each argument or value on its own line.") });
        sections.Children.Add(validation); sections.Children.Add(Field("Argument preview", preview));
        foreach (var box in new[] { name, description, path, extra }) box.TextChanged += (_, _) => Changed();
        var root = new DockPanel { LastChildFill = true };
        var buttons = Row(Button("Save profile", () => _vm.RunAsync(async () => { await _vm.SaveProfileAsync(Read()); _dirty = false; Models(); }), true),
            Button("Optimize…", async () => { try { await OptimizeAsync(Read()); } catch (Exception ex) { _vm.Notice = ex.Message; } }), Button("Cancel", () => NavigateAsync(Models)));
        buttons.Margin = new Thickness(0, 16, 0, 0); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(PageScroll(sections)); SetPage(root); Changed(); _dirty = false;
        Dispatcher.UIThread.Post(() => editingReady = true, DispatcherPriority.Background);
    }

    private void Settings()
    {
        var editingReady = false;
        var settings = _vm.Configuration.Settings;
        var exe = Input(settings.ServerPath, "C:\\llama.cpp\\llama-server.exe");
        var models = Input(settings.ModelsDirectory, "D:\\Models");
        var port = Input(settings.Port.ToString()); var timeout = Input(settings.StartupTimeoutSeconds.ToString());
        var windows = new CheckBox { Content = "Start the application when signing in to Windows", IsChecked = settings.StartWithWindows };
        var auto = new CheckBox { Content = "Start the selected model when the application opens", IsChecked = settings.AutoStartModel };
        var minimized = new CheckBox { Content = "Start minimized to the system tray", IsChecked = settings.StartMinimized };
        var version = Text("", "muted");
        var page = Stack(Text("Settings", "title"), Text("General application and local server settings.", "muted"),
            Card(Stack(Field("llama-server.exe", exe), Row(Button("Browse executable…", () => _vm.RunAsync(async () => { var path = await PickFileAsync("Select llama-server.exe", ["*.exe"]); if (path is not null) exe.Text = path; })),
                Button("Check server", () => _vm.RunAsync(async () => { version.Text = "Checking…"; var capabilities = await _vm.ProbeAsync(exe.Text ?? ""); version.Text = capabilities.Version + $"\nRecognized options: {capabilities.Flags.Count}"; })),
                Button("Install / update llama.cpp…", async () =>
                {
                    _installations = new ServerInstallationsWindow(_vm.DataDirectory, exe.Text ?? "");
                    try
                    {
                        var path = await _installations.ShowDialog<string?>(this);
                        if (path is not null) { exe.Text = path; version.Text = "Build selected. Save settings to use it on the next start or restart."; }
                    }
                    finally { _installations = null; }
                })), version,
                Field("Models folder", models), Button("Browse folder…", () => _vm.RunAsync(async () =>
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Models folder", AllowMultiple = false });
                    if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) models.Text = path;
                })))),
            Card(Stack(Field("API port on 127.0.0.1", port), Field("Loading timeout, seconds", timeout), windows, auto, minimized)),
            Row(Button("Save settings", () => _vm.RunAsync(async () =>
            {
                if (!int.TryParse(port.Text, out var p) || !int.TryParse(timeout.Text, out var t)) throw new ArgumentException("Port and timeout must be integers.");
                await _vm.SaveSettingsAsync(new AppSettings { ServerPath = exe.Text?.Trim() ?? "", ModelsDirectory = models.Text?.Trim() ?? "", Port = p, StartupTimeoutSeconds = t, StartWithWindows = windows.IsChecked == true, AutoStartModel = auto.IsChecked == true, StartMinimized = minimized.IsChecked == true });
                _dirty = false;
            }), true), Button("Configuration folder", () => _vm.RunAsync(() => { Directory.CreateDirectory(_vm.DataDirectory); Open(_vm.DataDirectory); return Task.CompletedTask; }))),
            Text("Closing the window keeps the application in the system tray. Exit stops the server. Stop interrupts active requests.", "muted"));
        foreach (var box in new[] { exe, models, port, timeout }) box.TextChanged += (_, _) => { if (editingReady && box.IsAttachedToVisualTree()) _dirty = true; };
        foreach (var check in new[] { windows, auto, minimized }) check.IsCheckedChanged += (_, _) => { if (editingReady && check.IsAttachedToVisualTree()) _dirty = true; };
        SetPage(PageScroll(page)); _dirty = false;
        Dispatcher.UIThread.Post(() => editingReady = true, DispatcherPriority.Background);
    }

    private ServerInstallationsWindow? _installations;
    public Task CancelInstallationAsync() => _installations?.CancelAndWaitAsync() ?? Task.CompletedTask;

    private async Task<string?> PickFileAsync(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private async Task OptimizeAsync(ModelProfile profile)
    {
        if (_vm.IsBusy || _vm.IsOptimizing || _vm.IsReadOnly || _vm.Status.State is ServerState.Starting or ServerState.Stopping)
        { _vm.Notice = "Optimization is unavailable while loading, stopping, or another operation is running."; return; }
        await new OptimizationWindow(_vm, profile).ShowDialog(this);
    }
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window { Title = title, Width = 480, Height = 220, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Text(message), Row(Button("Cancel", () => { dialog.Close(false); return Task.CompletedTask; }), Button("Continue", () => { dialog.Close(true); return Task.CompletedTask; }, true))) };
        return await dialog.ShowDialog<bool>(this);
    }
}


