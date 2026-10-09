using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Desktop;

public sealed class OptimizationWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ModelProfile _source;
    private readonly TextBlock _status = Text("Choose a workload, then preview the candidates.");
    private readonly StackPanel _results = new() { Spacing = 12 };
    private readonly StackPanel _inputs = new() { Spacing = 12 };
    private readonly TextBox _context, _reserve = Input("1024"), _tokens = Input("128"), _repetitions = Input("3"), _timeout = Input("180");
    private readonly TextBox _prompt = new() { Text = new OptimizationOptions().Prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Optimize settings", "Compare saved profiles" }, SelectedIndex = 0 };
    private readonly ComboBox _goal = new() { ItemsSource = new[] { "Balanced", "Generation speed", "Prompt processing speed" }, SelectedIndex = 0 };
    private readonly CheckBox _gpu = Check("Try GPU layers auto / all", true), _batch = Check("Try batch / microbatch sizes", true),
        _cache = Check("Allow KV precision changes (Q8 / Q4 + Flash Attention)", false), _mtp = Check("Try MTP on / off (requires compatible weights)", false);
    private readonly List<(CheckBox Check, ModelProfile Profile)> _comparisons = [];
    private readonly Button _preview = new() { Content = "Preview candidates" }, _run = new() { Content = "Run benchmark", IsEnabled = false, Classes = { "primary" } },
        _cancel = new() { Content = "Cancel run", IsEnabled = false };
    private IReadOnlyList<OptimizationCandidate>? _candidates;
    private OptimizationOptions? _options;
    private string? _planKey;
    private bool _running, _closeRequested;

    public OptimizationWindow(MainViewModel vm, ModelProfile source)
    {
        _vm = vm; _source = OptimizationPlan.Copy(source);
        Title = "Optimize model performance"; Width = 1020; Height = 900; MinWidth = 760; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _context = Input(source.Options.ContextSize is > 0 ? source.Options.ContextSize.Value.ToString(CultureInfo.InvariantCulture) : "8192");
        _context.Name = "OptimizationContext"; _reserve.Name = "OptimizationReserve"; _tokens.Name = "OptimizationTokens";
        _repetitions.Name = "OptimizationRepetitions"; _gpu.Name = "OptimizationGpu"; _results.Name = "OptimizationResults";
        _mode.Name = "OptimizationMode";
        _status.Name = "OptimizationStatus";
        var heading = Text("Optimize model performance"); heading.FontSize = 25; heading.FontWeight = FontWeight.SemiBold;
        _inputs.Children.Add(Text(source.Name));
        _inputs.Children.Add(Row(Field("Mode", _mode), Field("Ranking goal", _goal)));
        _inputs.Children.Add(Row(Field("Context tokens (fixed)", _context), Field("GPU reserve, MiB", _reserve), Field("Output tokens", _tokens)));
        _inputs.Children.Add(Row(Field("Measurements per candidate", _repetitions), Field("Request timeout, seconds", _timeout)));
        _inputs.Children.Add(Text("Reserve is checked on every NVIDIA GPU using sampled whole-device memory. Set 0 to disable this constraint. Context and one request slot stay fixed for all candidates."));
        _inputs.Children.Add(_gpu); _inputs.Children.Add(_batch); _inputs.Children.Add(_cache); _inputs.Children.Add(_mtp);
        var comparisons = new StackPanel { Spacing = 8 };
        foreach (var profile in vm.Profiles.Where(p => string.Equals(p.ModelPath, source.ModelPath, StringComparison.OrdinalIgnoreCase)))
        {
            var check = Check(profile.Name, profile.Id == source.Id); _comparisons.Add((check, profile)); comparisons.Children.Add(check);
            check.IsCheckedChanged += (_, _) => InvalidatePlan();
        }
        var compareProfiles = new Expander { Header = "Saved profiles for comparison (same model file)", Content = comparisons, IsEnabled = false };
        _inputs.Children.Add(compareProfiles);
        _inputs.Children.Add(new Expander { Header = "Benchmark prompt", Content = _prompt });
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(heading); body.Children.Add(_inputs);
        body.Children.Add(Text("Running temporarily stops the current model and interrupts its requests. Pause other clients. One warm-up is excluded; measurements use the same prompt, no prompt cache, greedy sampling, and a fixed output length. The previous server settings are restored afterward, including after cancellation; conversation caches are lost."));
        body.Children.Add(Row(_preview, _run, _cancel)); body.Children.Add(_status); body.Children.Add(_results);
        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = body } };
        foreach (var input in new[] { _context, _reserve, _tokens, _repetitions, _timeout, _prompt }) input.TextChanged += (_, _) => InvalidatePlan();
        foreach (var check in new[] { _gpu, _batch, _cache, _mtp }) check.IsCheckedChanged += (_, _) => InvalidatePlan();
        _mode.SelectionChanged += (_, _) =>
        {
            foreach (var check in new[] { _gpu, _batch, _cache, _mtp }) check.IsEnabled = _mode.SelectedIndex == 0;
            compareProfiles.IsEnabled = compareProfiles.IsExpanded = _mode.SelectedIndex == 1;
            InvalidatePlan();
        };
        _goal.SelectionChanged += (_, _) => InvalidatePlan();
        _preview.Click += async (_, _) => await PreviewAsync();
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => { _vm.CancelOptimization(); _status.Text = "Canceling… The previous session will be restored."; _cancel.IsEnabled = false; };
        Closing += (_, e) => { if (_running) { e.Cancel = true; _closeRequested = true; _vm.CancelOptimization(); _status.Text = "Canceling before closing…"; } };
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBox Input(string value) => new() { Text = value, Width = 185 };
    private static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value };
    private static StackPanel Row(params Control[] controls)
    { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }; foreach (var c in controls) row.Children.Add(c); return row; }
    private static StackPanel Field(string label, Control control) => new() { Spacing = 5, Children = { Text(label), control } };
    private string PlanKey() => System.Text.Json.JsonSerializer.Serialize(new { Options = ReadOptions(), Mode = _mode.SelectedIndex,
        Profiles = _comparisons.Where(p => p.Check.IsChecked == true).Select(p => p.Profile.Id).ToArray() });
    private void InvalidatePlan()
    {
        if (_running) return;
        // Avalonia can deliver TextChanged after the preview was prepared. Only actual edits invalidate it.
        try { if (_candidates is not null && _planKey == PlanKey()) return; }
        catch (ArgumentException) { }
        _candidates = null; _run.IsEnabled = false; _results.Children.Clear();
    }
    private OptimizationOptions ReadOptions()
    {
        int Number(TextBox input) => int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("Numeric fields must contain whole numbers.");
        var options = new OptimizationOptions { ContextSize = Number(_context), GpuReserveMiB = Number(_reserve), GeneratedTokens = Number(_tokens),
            Repetitions = Number(_repetitions), RequestTimeoutSeconds = Number(_timeout), Goal = (OptimizationGoal)_goal.SelectedIndex,
            TuneGpuLayers = _gpu.IsChecked == true, TuneBatch = _batch.IsChecked == true, TuneCache = _cache.IsChecked == true,
            TuneMtp = _mtp.IsChecked == true, Prompt = _prompt.Text ?? "" };
        options.Validate(); return options;
    }
    private async Task PreviewAsync()
    {
        _inputs.IsEnabled = false; _preview.IsEnabled = false; _run.IsEnabled = false; _results.Children.Clear();
        try
        {
            _options = ReadOptions();
            _planKey = PlanKey();
            var capabilities = await _vm.ProbeAsync(_vm.Configuration.Settings.ServerPath);
            if (_mode.SelectedIndex == 0) _candidates = OptimizationPlan.Create(_source, _options, capabilities.Flags);
            else
            {
                _candidates = _comparisons.Where(p => p.Check.IsChecked == true).Select(p =>
                {
                    var copy = OptimizationPlan.Copy(p.Profile); copy.Options.ContextSize = _options.ContextSize; copy.Options.Parallel = 1;
                    return new OptimizationCandidate(p.Profile.Name, copy);
                }).ToArray();
            }
            if (_candidates.Count is < 1 or > 12) throw new ArgumentException("Select 1–12 profiles to compare.");
            foreach (var candidate in _candidates)
            {
                var args = Arguments.Build(_vm.Configuration.Settings, candidate.Profile); capabilities.Validate(args);
                _results.Children.Add(new Expander { Header = candidate.Label, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = Text(Arguments.Preview(_vm.Configuration.Settings.ServerPath, args)) });
            }
            _status.Text = $"{_candidates.Count} candidates · 1 warm-up + {_options.Repetitions} measured requests each. " +
                (_mode.SelectedIndex == 0 ? "Only explicitly enabled groups vary. This is a bounded search, not a guarantee of globally optimal settings." :
                    "Saved profile options are preserved except for the displayed fixed context and one slot.");
            _run.IsEnabled = true;
        }
        catch (Exception ex) { _status.Text = ex.Message; _candidates = null; }
        finally { _inputs.IsEnabled = true; _preview.IsEnabled = true; }
    }
    private async Task RunAsync()
    {
        if (_candidates is null || _options is null || _running) return;
        _running = true; _inputs.IsEnabled = false; _preview.IsEnabled = false; _run.IsEnabled = false; _cancel.IsEnabled = true;
        _results.Children.Clear();
        try
        {
            var report = await _vm.OptimizeAsync(_candidates, _options, new Progress<string>(text => _status.Text = text));
            Present(report);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally
        {
            _running = false; _inputs.IsEnabled = true; _preview.IsEnabled = true; _cancel.IsEnabled = false;
            if (_closeRequested) Close();
        }
    }
    public void Present(OptimizationReport report)
    {
        _results.Children.Clear();
        var best = OptimizationPlan.Best(report.Results, report.Options.Goal);
        _status.Text = (report.Canceled ? "Canceled. Completed measurements are shown below." : "Benchmark complete.") +
            " " + (report.RestorationError ?? "Previous server state restored.") +
            (best is null ? " No eligible result." : $" Best measured candidate: {best.Candidate.Label}.");
        var reports = new Button { Content = "Open reports folder" };
        reports.Click += (_, _) =>
        {
            try
            {
                var path = System.IO.Path.Combine(_vm.DataDirectory, "benchmarks"); System.IO.Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        _results.Children.Add(reports);
        foreach (var result in report.Results)
        {
            string Number(double? value) => value?.ToString("N1", CultureInfo.InvariantCulture) ?? "—";
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Text((ReferenceEquals(result, best) ? "★ " : "") + result.Candidate.Label));
            panel.Children.Add(Text(result.Samples.Count == 0 ? "No completed measurements." :
                $"Generation {Number(result.GenerationSpeed)} t/s · Prompt {Number(result.PromptSpeed)} t/s · First token {Number(result.FirstTokenMilliseconds)} ms (medians)"));
            panel.Children.Add(Text($"Sampled GPU peak {Number(result.PeakGpuMiB)} MiB · Minimum free per GPU {Number(result.MinimumGpuFreeMiB)} MiB · RAM peak {Number(result.PeakRamMiB)} MiB"));
            if (result.Error is { } error) panel.Children.Add(Text("Excluded: " + error));
            panel.Children.Add(new Expander { Header = "Profile arguments", Content = Text(Arguments.Preview(_vm.Configuration.Settings.ServerPath,
                Arguments.Build(_vm.Configuration.Settings, result.Candidate.Profile))) });
            var save = new Button { Content = "Save as new profile", IsEnabled = result.Eligible && !_vm.IsReadOnly };
            save.Click += async (_, _) =>
            {
                try
                {
                    var profile = OptimizationPlan.Copy(result.Candidate.Profile); profile.Id = Guid.NewGuid();
                    profile.Name = _source.Name + " · optimized " + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
                    profile.Description += $"\nBenchmark: {result.Candidate.Label}; {result.GenerationSpeed:F1} t/s generation, {result.PromptSpeed:F1} t/s prompt.";
                    await _vm.SaveProfileAsync(profile); save.IsEnabled = false; save.Content = "Saved: " + profile.Name;
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            panel.Children.Add(save);
            _results.Children.Add(new Border { Classes = { "card" }, Child = panel });
        }
    }
}
