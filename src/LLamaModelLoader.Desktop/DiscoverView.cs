using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

/// <summary>Searches public GGUF repositories on Hugging Face, estimates how each quantization fits this machine, and queues downloads into the models folder.</summary>
public sealed class DiscoverView : UserControl
{
    private const string Unavailable = "Hugging Face is unavailable. Check the connection and try again.";
    private const string RateLimited = "Hugging Face refused the request (rate limit). Try again later.";
    private const string NoFolder = "Set a models folder in Settings first.";
    private static readonly string[] KvTypes = ["f16", "q8_0", "q4_0"];
    private static readonly (string Label, HfSort Sort)[] Sorts = [("Most downloaded", HfSort.Downloads), ("Trending", HfSort.Trending), ("Most liked", HfSort.Likes), ("Recently updated", HfSort.LastModified)];
    private readonly MainViewModel _vm;
    private readonly Action<ModelProfile> _createProfile;
    private readonly HuggingFaceClient _client;
    private readonly long _ram;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<HfModelSummary> _models = [];
    private readonly TextBox _search = new() { Name = "DiscoverSearch", PlaceholderText = "Search GGUF models on Hugging Face…  Leave empty for the most popular." },
        _manualVram = new() { Name = "DiscoverManualVram", Width = 120, PlaceholderText = "Detected" };
    private readonly ComboBox _sort = new() { Name = "DiscoverSort", ItemsSource = Sorts.Select(s => s.Label).ToArray(), SelectedIndex = 0, Width = 170 },
        _context = new() { Name = "DiscoverContext", Width = 130 }, _kvType = new() { Name = "DiscoverKvType", ItemsSource = KvTypes, SelectedIndex = 0, Width = 110 };
    private readonly ListBox _results = new() { Name = "DiscoverResults", MaxHeight = 540 };
    private readonly Button _searchButton = new() { Content = "Search", Classes = { "primary" } }, _more = new() { Content = "Load more", IsVisible = false },
        _open = new() { Content = "Open on Hugging Face", IsVisible = false };
    private readonly TextBlock _status = Text("Search public GGUF repositories. Downloads go to the models folder from Settings.", "muted"),
        _title = Text("Select a repository"), _info = Text("", "muted"), _note = Text("", "muted"), _gpu = Text("Detecting GPU memory…", "muted");
    private readonly StackPanel _quantizations = new() { Name = "DiscoverQuantizations", Spacing = 8 }, _queue = new() { Name = "DiscoverQueue", Spacing = 12 };
    private readonly Control _settings;
    private readonly Dictionary<DownloadItem, (ProgressBar Bar, TextBlock Text)> _queueRows = [];
    private List<(DownloadItem Item, DownloadItemState State)> _queueStates = [];
    private HfModelDetails? _selected;
    private GgufKvShape? _shape;
    private GpuMemory? _gpuMemory;
    private string? _nextPage;
    private int _searchVersion, _detailsVersion, _queuePending;

    public DiscoverView(MainViewModel vm, Action<ModelProfile> createProfile, Func<CancellationToken, Task<(GpuMemory?, string?)>>? readGpu = null, Func<long>? readRam = null)
    {
        _vm = vm; _createProfile = createProfile; _client = new HuggingFaceClient(vm.HubHttp); _ram = (readRam ?? GpuInfo.TotalRamBytes)();
        _status.Name = "DiscoverStatus"; _title.Name = "DiscoverTitle"; _gpu.Name = "DiscoverGpu";
        _title.FontSize = 18; _title.FontWeight = FontWeight.SemiBold;
        _results.ItemsSource = _models;
        _results.ItemTemplate = new FuncDataTemplate<HfModelSummary>((model, _) => model is null ? new TextBlock() : ResultRow(model));
        _settings = Stack(Row(Field("Context", _context), Field("KV cache type", _kvType), Field("Manual VRAM (GiB)", _manualVram)), _gpu);
        _settings.IsVisible = false;
        var searchRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_searchButton, Dock.Right); DockPanel.SetDock(_sort, Dock.Right);
        _searchButton.Margin = _sort.Margin = new Thickness(10, 0, 0, 0);
        searchRow.Children.Add(_searchButton); searchRow.Children.Add(_sort); searchRow.Children.Add(_search);
        var columns = new Grid { ColumnDefinitions = new("290,*") };
        var left = Stack(_results, _more); left.Margin = new Thickness(0, 0, 16, 0);
        var heading = new DockPanel { LastChildFill = true };
        var name = Stack(_title, _info); name.Spacing = 2;
        DockPanel.SetDock(_open, Dock.Right); _open.VerticalAlignment = VerticalAlignment.Top; heading.Children.Add(_open); heading.Children.Add(name);
        var details = Stack(heading, _note, _settings); details.Spacing = 8;
        var right = Stack(new Border { Classes = { "card" }, Padding = new Thickness(18, 14), Child = details }, _quantizations); right.Spacing = 8; Grid.SetColumn(right, 1);
        columns.Children.Add(left); columns.Children.Add(right);
        var page = Stack(Text("Discover models", "title"), _status, searchRow, columns, Card(Stack(Text("Downloads"), _queue)));
        page.Margin = new Thickness(0, 0, 12, 0);
        Content = new ScrollViewer { AllowAutoHide = false, Content = page };
        _searchButton.Click += (_, _) => _ = SearchAsync(false);
        _more.Click += (_, _) => _ = SearchAsync(true);
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(false); } };
        _sort.SelectionChanged += (_, _) => { if (_models.Count > 0) _ = SearchAsync(false); };
        _results.SelectionChanged += (_, _) => { if (_results.SelectedItem is HfModelSummary model && model.Id != _selected?.Id) _ = SelectAsync(model); };
        _context.SelectionChanged += (_, _) => RenderQuantizations();
        _kvType.SelectionChanged += (_, _) => RenderQuantizations();
        _manualVram.TextChanged += (_, _) => RenderQuantizations();
        _open.Click += (_, _) => { if (_selected is { } details) Process.Start(new ProcessStartInfo($"https://huggingface.co/{details.Id}") { UseShellExecute = true }); };
        _ = ReadGpuAsync(readGpu ?? (token => GpuInfo.ReadAsync(null, token)));
    }

    public static ModelProfile ProfileFor(string repoId, HfQuantization quant, string path, int context, string kvType, FitResult? fit)
    {
        var options = new LlamaOptions { ContextSize = context, GpuLayers = fit?.Level switch
        {
            FitLevel.Fits => "all",
            FitLevel.Partial => fit.GpuLayers?.ToString(CultureInfo.InvariantCulture),
            _ => null,
        } };
        if (!kvType.Equals("f16", StringComparison.OrdinalIgnoreCase)) options.CacheTypeK = options.CacheTypeV = kvType;
        return new ModelProfile { Name = $"{repoId[(repoId.IndexOf('/') + 1)..]}-{quant.Label}".Replace(",", ""), ModelPath = path, Options = options };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm.Downloads.Changed += QueueChanged; RenderQueue();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _vm.Downloads.Changed -= QueueChanged; _lifetime.Cancel();
    }

    private int Context => _context.SelectedItem is int context ? context : FitEstimator.DefaultContext(_selected?.ContextLength);
    private string KvType => _kvType.SelectedItem as string ?? "f16";
    private long? Vram => double.TryParse(_manualVram.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var gib) && gib > 0 && gib < 1 << 20
        ? (long)(gib * (1L << 30)) : _gpuMemory?.TotalBytes;
    private string? ModelsFolder => _vm.Configuration.Settings.ModelsDirectory is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) && Directory.Exists(folder) ? folder : null;

    private async Task SearchAsync(bool more)
    {
        var next = more ? _nextPage : null;
        if (more && next is null) return;
        var version = ++_searchVersion;
        _searchButton.IsEnabled = _more.IsEnabled = false; _status.Text = "Searching Hugging Face…";
        try
        {
            var (models, nextPage) = await _client.SearchAsync(_search.Text ?? "", Sorts[Math.Max(0, _sort.SelectedIndex)].Sort, next, _lifetime.Token);
            if (version != _searchVersion) return;
            if (!more) _models.Clear();
            foreach (var model in models) if (_models.All(m => m.Id != model.Id)) _models.Add(model);
            _nextPage = nextPage; _more.IsVisible = nextPage is not null;
            _status.Text = _models.Count == 0 ? "No GGUF repositories match this search." : $"{_models.Count} repositories. Select one to see its quantizations.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _searchVersion) _status.Text = Describe(ex); }
        finally { if (version == _searchVersion) _searchButton.IsEnabled = _more.IsEnabled = true; }
    }

    private async Task SelectAsync(HfModelSummary model)
    {
        var version = ++_detailsVersion;
        _selected = null; _shape = null; _title.Text = model.Id; _info.Text = "Loading the file list…"; _note.Text = "";
        _open.IsVisible = _settings.IsVisible = false; RenderQuantizations();
        try
        {
            var details = await _client.GetDetailsAsync(model.Id, _lifetime.Token);
            if (version != _detailsVersion) return;
            _selected = details; _title.Text = details.Id;
            _info.Text = string.Join(" · ", new[] { details.Architecture, details.Parameters is { } p ? $"{p / 1e9:N1}B parameters" : null,
                details.ContextLength is { } max ? $"max context {max:N0}" : null, details.License is { } license ? "licence " + license : null }.Where(x => x is not null));
            _context.ItemsSource = FitEstimator.ContextChoices(details.ContextLength);
            _context.SelectedItem = FitEstimator.DefaultContext(details.ContextLength);
            _open.IsVisible = _settings.IsVisible = true;
            RenderQuantizations();
            if (details.Gated) { _note.Text = "Gated repository: its files need an accepted licence and a signed-in account, so it cannot be downloaded here."; return; }
            if (details.Quantizations.Count == 0) { _note.Text = "No downloadable GGUF files were found in this repository."; return; }
            _note.Text = "Reading the GGUF header for a context-aware estimate…";
            var (shape, failure) = await ReadShapeAsync(details);
            if (version != _detailsVersion) return;
            _shape = shape;
            _note.Text = shape is null ? $"Size-only estimates: the GGUF header could not be read. {failure}"
                : $"{shape.Layers} layers · KV cache estimated from the GGUF header" + (shape.Approximate ? " · approximate for this architecture" : "");
            RenderQuantizations();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _detailsVersion) return;
            _info.Text = ""; _status.Text = Describe(ex);
        }
    }

    // The KV shape is the same for every quantization of a repository, so the smallest file's header is enough.
    private async Task<(GgufKvShape?, string?)> ReadShapeAsync(HfModelDetails details)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { return (await RemoteGguf.ReadKvShapeAsync(_vm.HubHttp, details.Id, details.Sha, details.Quantizations.MinBy(q => q.TotalBytes)!.FirstPath, timeout.Token), null); }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { return (null, "Hugging Face did not respond in time."); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, Describe(ex)); }
    }

    private async Task ReadGpuAsync(Func<CancellationToken, Task<(GpuMemory?, string?)>> read)
    {
        try
        {
            var (gpu, error) = await read(_lifetime.Token);
            _gpuMemory = gpu;
            _gpu.Text = gpu is null ? $"{error ?? "No GPU memory information is available."} Enter the VRAM manually."
                : $"{gpu.Name} · {GiB(gpu.TotalBytes):N2} GiB ({GiB(gpu.UsedBytes):N2} GiB in use)";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) { _gpu.Text = ex.Message + " Enter the VRAM manually."; }
        RenderQuantizations();
    }

    private void RenderQuantizations()
    {
        _quantizations.Children.Clear();
        if (_selected is not { } details) return;
        var folder = ModelsFolder;
        if (folder is null && !details.Gated) _status.Text = NoFolder;
        var downloader = folder is null ? null : new ModelDownloader(folder, _vm.HubHttp);
        foreach (var quant in details.Quantizations)
        {
            var fit = Estimate(quant);
            var label = new TextBlock { Text = quant.Label, FontWeight = FontWeight.SemiBold, Width = 100, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var size = new TextBlock { Text = Size(quant.TotalBytes) + (quant.Parts.Count > 1 ? $" · {quant.Parts.Count} parts" : ""), Classes = { "muted" }, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var badgeText = new TextBlock { Name = "Badge" + quant.Label, Text = BadgeText(fit), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
            var badge = new Border { Background = BadgeBrush(fit.Level), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
            var breakdown = Text(Breakdown(fit), "muted"); breakdown.Name = "Breakdown" + quant.Label;
            var grid = new Grid { ColumnDefinitions = new("*,Auto") };
            var estimate = Stack(Row(label, size, badge), breakdown); estimate.Spacing = 4; estimate.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(estimate);
            if (!details.Gated) { var action = DownloadAction(details, quant, downloader); Grid.SetColumn(action, 1); grid.Children.Add(action); }
            _quantizations.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(18, 10), Child = grid });
        }
    }

    private Control DownloadAction(HfModelDetails details, HfQuantization quant, ModelDownloader? downloader)
    {
        var button = new Button { Name = "Download" + quant.Label, Content = "Download", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        if (downloader is null) return button;
        var active = _vm.Downloads.Items.FirstOrDefault(i => i.RepoId.Equals(details.Id, StringComparison.OrdinalIgnoreCase) && i.Quantization.Label == quant.Label &&
            i.Quantization.FirstPath == quant.FirstPath && i.State is DownloadItemState.Queued or DownloadItemState.Downloading);
        if (active is not null) { button.Content = active.State == DownloadItemState.Queued ? "Queued" : "Downloading…"; return button; }
        try
        {
            switch (downloader.GetState(details.Id, quant).State)
            {
                case LocalModelState.Downloaded: button.Content = "Downloaded"; return button;
                case LocalModelState.Conflict:
                    var path = quant.Parts.Select(p => (File: p, Path: downloader.Destination(details.Id, p)))
                        .First(p => Directory.Exists(p.Path) || (File.Exists(p.Path) && new FileInfo(p.Path).Length != p.File.Size)).Path;
                    return Stack(button, Text($"A different file already exists at {path}.", "muted"));
                case LocalModelState.Partial: button.Content = "Resume"; break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return Stack(button, Text(ex.Message, "muted")); }
        button.IsEnabled = true;
        button.Click += (_, _) =>
        {
            try { _vm.Downloads.Enqueue(details.Id, details.Sha, quant, Context, KvType, Estimate(quant)); }
            catch (InvalidOperationException ex) { _status.Text = ex.Message; }
        };
        return button;
    }

    private FitResult Estimate(HfQuantization quant) => FitEstimator.Estimate(new(quant.TotalBytes, _shape, Context, KvType, Vram, _ram));

    private string BadgeText(FitResult fit) => (fit.Approximate ? "≈ " : "") + fit.Level switch
    {
        FitLevel.Fits => "Fits in VRAM",
        FitLevel.Partial => fit.GpuLayers is { } layers && _shape is { } shape ? $"Partial offload · {layers}/{shape.Layers} layers" : "Partial offload",
        FitLevel.DoesNotFit => "Does not fit",
        _ => "Unknown",
    } + (fit.ContextAware ? "" : " · size-only estimate");

    private string Breakdown(FitResult fit) =>
        $"weights {GiB(fit.WeightBytes):N1}" + (fit.KvBytes is { } kv ? $" + KV {GiB(kv):N1}" : "") +
        $" + buffers {GiB(fit.ComputeBytes):N1} + reserve {GiB(fit.ReserveBytes):N1} = {GiB(fit.TotalBytes):N1} GiB" + (Vram is { } vram ? $" of {GiB(vram):N1}" : "");

    private static IBrush BadgeBrush(FitLevel level) => SolidColorBrush.Parse(level switch
    {
        FitLevel.Fits => "#2E7D5B",
        FitLevel.Partial => "#9A6B12",
        FitLevel.DoesNotFit => "#8E3B3B",
        _ => "#4A5568",
    });

    // Raised on the queue's worker thread, once per progress report; posting at most one redraw per batch keeps the UI thread free.
    private void QueueChanged(DownloadItem item)
    {
        if (Interlocked.Exchange(ref _queuePending, 1) == 1) return;
        Dispatcher.UIThread.Post(() => { Volatile.Write(ref _queuePending, 0); if (!_lifetime.IsCancellationRequested) RenderQueue(); });
    }

    // Rows are rebuilt only when items or their states change, so progress updates do not recreate buttons under the pointer.
    private void RenderQueue()
    {
        var items = _vm.Downloads.Items;
        var states = items.Select(i => (i, i.State)).ToList();
        if (!states.SequenceEqual(_queueStates))
        {
            _queueStates = states; _queueRows.Clear(); _queue.Children.Clear();
            foreach (var item in items) _queue.Children.Add(QueueRow(item));
            if (items.Count == 0) _queue.Children.Add(Text("No downloads yet. Downloads run one at a time; a paused download keeps its partial file for resuming.", "muted"));
            RenderQuantizations();
        }
        foreach (var item in items) if (_queueRows.TryGetValue(item, out var row)) UpdateRow(item, row.Bar, row.Text);
    }

    private Control QueueRow(DownloadItem item)
    {
        var title = Text($"{item.RepoId} · {item.Quantization.Label} · {item.State switch { DownloadItemState.Completed => "Downloaded", var state => state.ToString() }}");
        title.FontWeight = FontWeight.SemiBold;
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, IsVisible = item.State != DownloadItemState.Completed };
        var text = Text("", "muted");
        var buttons = Row();
        var queue = _vm.Downloads;
        switch (item.State)
        {
            case DownloadItemState.Queued or DownloadItemState.Downloading:
                buttons.Children.Add(Button("Pause", () => queue.Pause(item)));
                break;
            case DownloadItemState.Paused or DownloadItemState.Failed:
                buttons.Children.Add(Button("Resume", () => queue.Resume(item)));
                break;
            case DownloadItemState.Completed when item.FinalPath is { } path:
                buttons.Children.Add(Button("Create profile", () => _createProfile(ProfileFor(item.RepoId, item.Quantization, path, item.ContextSize, item.KvType, item.Fit)), true));
                break;
        }
        var remove = Button(item.State == DownloadItemState.Completed ? "Remove from list" : "Remove", async () =>
        {
            if (item.State == DownloadItemState.Completed || await ConfirmAsync("Remove this download and delete its partial file? The finished parts are kept.")) queue.Remove(item);
        });
        buttons.Children.Add(remove);
        _queueRows[item] = (bar, text);
        UpdateRow(item, bar, text);
        return Stack(title, bar, text, buttons);
    }

    private static void UpdateRow(DownloadItem item, ProgressBar bar, TextBlock text)
    {
        var progress = item.Progress;
        var total = progress?.TotalBytes ?? item.Quantization.TotalBytes;
        var done = item.State == DownloadItemState.Completed ? total : progress?.DownloadedBytes ?? 0;
        bar.Value = total > 0 ? 100d * done / total : 0;
        var line = $"{GiB(done):N2} / {GiB(total):N2} GiB";
        if (item.State == DownloadItemState.Downloading && progress is { BytesPerSecond: > 0 } p)
        {
            line += $" · {p.BytesPerSecond / (1 << 20):N1} MiB/s · {TimeSpan.FromSeconds(Math.Ceiling((total - done) / p.BytesPerSecond)):hh\\:mm\\:ss} left";
            if (p.Parts > 1) line += $" · part {p.Part} of {p.Parts}";
        }
        text.Text = item.Message is { Length: > 0 } message && item.State != DownloadItemState.Completed ? line + "\n" + message : item.State == DownloadItemState.Completed ? item.FinalPath : line;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var dialog = new Window { Title = "Remove download", Width = 480, Height = 200, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Text(message), Row(Button("Cancel", () => dialog.Close(false)), Button("Remove", () => dialog.Close(true), true))) };
        return await dialog.ShowDialog<bool>(owner);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { Message: RateLimited } => RateLimited,
        HttpRequestException => Unavailable,
        _ => ex.Message,
    };

    private static Control ResultRow(HfModelSummary model)
    {
        var name = new TextBlock { Text = model.Id, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var tags = Row();
        if (model.Gated) tags.Children.Add(Chip("gated"));
        if (model.IsVision) tags.Children.Add(Chip("vision"));
        var stats = Text($"{model.Downloads:N0} downloads · {model.Likes:N0} likes" + (model.LastModified is { } modified ? $" · {modified.LocalDateTime:d}" : ""), "muted");
        stats.FontSize = 12;
        var row = Stack(name, stats); row.Spacing = 3;
        if (tags.Children.Count > 0) row.Children.Add(tags);
        return row;
    }

    private static Border Chip(string text) => new()
    {
        Background = SolidColorBrush.Parse("#4A5568"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.White },
    };

    private static double GiB(long bytes) => bytes / 1073741824d;
    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{GiB(bytes):N2} GiB" : $"{bytes / 1048576d:N1} MiB";
    private static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary) button.Classes.Add("primary");
        button.Click += (_, _) => action();
        return button;
    }
    private static Button Button(string label, Func<Task> action) { var button = new Button { Content = label }; button.Click += async (_, _) => await action(); return button; }
    private static TextBlock Text(string value, string? style = null)
    {
        var control = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        if (style is not null) control.Classes.Add(style);
        return control;
    }
    private static StackPanel Stack(params Control[] controls) { var panel = new StackPanel { Spacing = 10 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
    private static StackPanel Row(params Control[] controls) { var panel = Stack(controls); panel.Orientation = Orientation.Horizontal; return panel; }
    private static Control Field(string label, Control control) { var field = Stack(Text(label, "muted"), control); field.Spacing = 2; return field; }
    private static Border Card(Control child) => new() { Classes = { "card" }, Child = child };
}
