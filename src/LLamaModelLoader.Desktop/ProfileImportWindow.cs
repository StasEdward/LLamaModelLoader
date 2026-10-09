using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

public sealed class ProfileImportWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly List<Entry> _entries = [];
    private readonly HashSet<Entry> _ambiguous = [];
    private readonly TextBlock _status = Text("Scanning the local models folder…");
    private readonly Button _import = new() { Name = "ImportSelectedProfiles", Content = "Import selected", Classes = { "primary" } };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _saving;
    private bool _scanning = true;
    private Task _scan = Task.CompletedTask;
    private sealed record Entry(ModelProfile Profile, CheckBox Include, TextBox Name, TextBox Path, TextBlock Info, TextBlock Preview);

    public ProfileImportWindow(MainViewModel vm, IReadOnlyList<ModelProfile> profiles)
    {
        _vm = vm; Title = "Import model profiles"; Width = 940; Height = 800; MinWidth = 720; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _status.Name = "ProfileImportStatus";
        var list = new StackPanel { Spacing = 14 };
        foreach (var profile in ProfileTransfer.PrepareImports(profiles, vm.Profiles))
        {
            var include = new CheckBox { Content = "Import this profile", IsChecked = true };
            var name = new TextBox { Text = profile.Name, Name = "ImportedProfileName" };
            var path = new TextBox { Text = profile.ModelPath, Name = "ImportedModelPath" };
            var info = Text(""); var preview = Text("");
            var entry = new Entry(profile, include, name, path, info, preview); _entries.Add(entry);
            var browse = new Button { Content = "Locate GGUF…" };
            browse.Click += async (_, _) =>
            {
                try
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Locate the local model", AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType("GGUF model") { Patterns = ["*.gguf"] }] });
                    if (files.FirstOrDefault()?.TryGetLocalPath() is { } file) path.Text = file;
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            include.IsCheckedChanged += (_, _) => Validate(); name.TextChanged += (_, _) => Validate(); path.TextChanged += (_, _) => Validate();
            list.Children.Add(new Border { Classes = { "card" }, Child = Stack(include, Text("Profile name"), name,
                Text("Local model path"), path, browse, info,
                new Expander { Header = "Settings and additional arguments", Content = Stack(Text(profile.Description), preview), HorizontalAlignment = HorizontalAlignment.Stretch }) });
        }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => { if (!_saving) Close(0); };
        var heading = Text("Import model profiles"); heading.FontSize = 25;
        var header = Stack(heading, Text("Selected profiles are added as new entries. Existing profiles, selection, server, and app settings are preserved. Matching names receive an imported suffix."),
            Text("Only filenames and settings are transferred. Locate missing models now, or import the profiles and choose their files in Edit later. Review additional arguments when moving between computers."));
        var footer = Stack(_status, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _import, cancel } });
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(24) };
        header.Margin = new Thickness(0, 0, 0, 14); footer.Margin = new Thickness(0, 14, 0, 0);
        grid.Children.Add(header); var scroll = new ScrollViewer { Content = list }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid;
        _import.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; list.IsEnabled = false; _import.IsEnabled = cancel.IsEnabled = false;
            int? count = null;
            try { count = await _vm.ImportProfilesAsync(ReadSelected()); }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _saving = false; list.IsEnabled = true; cancel.IsEnabled = true; Validate(); }
            if (count is not null) Close(count.Value);
        };
        Opened += (_, _) => _scan = ResolveModelsAsync();
        Closing += (_, e) => { if (_saving) e.Cancel = true; else _lifetime.Cancel(); };
        Closed += async (_, _) => { _lifetime.Cancel(); await _scan; _lifetime.Dispose(); };
        Validate();
    }

    private List<ModelProfile> ReadSelected() => _entries.Where(e => e.Include.IsChecked == true).Select(e => new ModelProfile
    { Name = e.Name.Text?.Trim() ?? "", Description = e.Profile.Description, ModelPath = e.Path.Text?.Trim() ?? "", Options = e.Profile.Options, ExtraArguments = e.Profile.ExtraArguments }).ToList();

    private void Validate()
    {
        var valid = _entries.Any(e => e.Include.IsChecked == true);
        foreach (var entry in _entries)
        {
            try
            {
                var draft = new ModelProfile { Name = entry.Name.Text ?? "", ModelPath = entry.Path.Text ?? "", Options = entry.Profile.Options, ExtraArguments = entry.Profile.ExtraArguments };
                entry.Preview.Text = Arguments.Preview("llama-server.exe", Arguments.Build(new(), draft));
                var model = ModelCatalog.Inspect(draft.ModelPath);
                entry.Info.Text = model.Error is null ? model.Summary : "Model is not ready: " + model.Error + " Set its path before starting.";
                if (_ambiguous.Contains(entry) && entry.Path.Text == entry.Profile.ModelPath)
                    entry.Info.Text = "Several local models have this filename. Choose the intended file with Locate GGUF.";
            }
            catch (ArgumentException ex) { entry.Info.Text = ex.Message; entry.Preview.Text = "Correct the profile fields to see the arguments."; if (entry.Include.IsChecked == true) valid = false; }
        }
        _import.IsEnabled = valid && !_saving && !_scanning && !_vm.IsReadOnly && !_vm.IsOptimizing;
    }

    private async Task ResolveModelsAsync()
    {
        try
        {
            var directory = _vm.Configuration.Settings.ModelsDirectory;
            if (!System.IO.Directory.Exists(directory)) { _status.Text = "No models folder configured. Locate model files now or after importing."; return; }
            var files = await ModelCatalog.ScanAsync(directory, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            foreach (var entry in _entries)
            {
                if (entry.Path.Text != entry.Profile.ModelPath) continue;
                var matches = files.Where(f => f.Name.Equals(System.IO.Path.GetFileName(entry.Profile.ModelPath), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) entry.Path.Text = matches[0].Path;
                else if (matches.Length == 0) entry.Path.Text = System.IO.Path.Combine(directory, entry.Profile.ModelPath);
                else _ambiguous.Add(entry);
            }
            _status.Text = "Review the selected profiles, then click Import selected.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Could not scan models: " + ex.Message; }
        finally { _scanning = false; if (!_lifetime.IsCancellationRequested) Validate(); }
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Stack(params Control[] controls) { var stack = new StackPanel { Spacing = 8 }; foreach (var c in controls) stack.Children.Add(c); return stack; }
}
