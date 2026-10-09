using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

public sealed class StatisticsWindow : Window
{
    private readonly MainViewModel _main;
    private readonly StatisticsClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _session = Label(""), _updated = Label("Waiting for data…"), _errors = Label("");
    private readonly TextBlock _generation = Value(), _prompt = Value(), _requests = Value(), _tokens = Value(), _cpu = Value(), _ram = Value();
    private readonly TextBlock _generationSession = new() { Name = "SessionGenerationRate", Text = "Session average: — tokens/s", FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8BE3C1") };
    private readonly StackPanel _gpus = new() { Spacing = 10 }, _slots = new() { Spacing = 8 };
    private readonly Button _pause = new() { Content = "Pause" };
    private readonly DispatcherTimer _stateTimer;
    private ServerStatus? _observed;
    private bool _paused;
    private Task _poll = Task.CompletedTask;
    private (TimeSpan Cpu, long Timestamp)? _previousCpu;

    public StatisticsWindow(MainViewModel main, bool autoRefresh = true)
    {
        _main = main;
        Title = "Statistics · Llama Model Loader"; Width = 1040; Height = 810; MinWidth = 780; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Server statistics", Classes = { "title" } }, _session } });
        Grid.SetColumn(_pause, 1); header.Children.Add(_pause);
        var cards = new Grid { ColumnDefinitions = new("*,*,*"), RowDefinitions = new("Auto,Auto") };
        ToolTip.SetTip(_generationSession, "Total generated tokens / total generation time since server start. Idle and prompt processing time are excluded. Counters may update after a request completes.");
        Control[] items = [Card("GENERATION", _generation, "Server rate · tokens/s", _generationSession), Card("PROMPT PROCESSING", _prompt, "Average speed · tokens/s"),
            Card("REQUESTS", _requests, "Processing / queued"), Card("SESSION TOKENS", _tokens, "Input / generated"),
            Card("SERVER CPU", _cpu, "Share of total CPU capacity"), Card("SERVER RAM", _ram, "Process tree working set")];
        for (var i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i % 3); Grid.SetRow(items[i], i / 3); cards.Children.Add(items[i]); }
        _errors.Foreground = Brush.Parse("#E4C58C");
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(28), Spacing = 18, Children = {
            header, _updated, cards, _errors,
            new TextBlock { Text = "NVIDIA GPU", FontSize = 19, FontWeight = FontWeight.SemiBold },
            Label("Whole-device readings, including other applications. Memory is shown in MiB."), _gpus,
            new TextBlock { Text = "Request slots", FontSize = 19, FontWeight = FontWeight.SemiBold },
            Label("Context is the slot capacity. Token counts come from /slots; they do not represent KV cache usage."), _slots,
            Label("Refreshes every 2 seconds. llama.cpp reports average speeds and counters, which may update after a request completes. A dash (—) means data is unavailable.")
        } } };
        _pause.Click += (_, _) => { _paused = !_paused; _previousCpu = null; _pause.Content = _paused ? "Continue" : "Pause"; _updated.Text = _paused ? "Paused · showing the last snapshot" : "Waiting for a new snapshot…"; };
        _stateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => CheckSession());
        if (autoRefresh) _stateTimer.Start();
        Opened += (_, _) => { if (autoRefresh) _poll = PollAsync(); };
        Closed += async (_, _) => { _stateTimer.Stop(); _lifetime.Cancel(); await _poll; _client.Dispose(); _lifetime.Dispose(); };
        CheckSession();
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private static TextBlock Value() => new() { Text = "—", FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#8BE3C1"), TextWrapping = TextWrapping.Wrap };
    private static Border Card(string title, TextBlock value, string hint, Control? extra = null)
    {
        var content = new StackPanel { Spacing = 10, Children = { Label(title), value, Label(hint) } };
        if (extra is not null) content.Children.Add(extra);
        return new() { Classes = { "card" }, Margin = new Thickness(0, 0, 10, 10), Child = content };
    }
    private static string N(double? value, string format = "N0") => value?.ToString(format) ?? "—";
    private static bool SameSession(ServerStatus a, ServerStatus b) => a.StartedAt == b.StartedAt && a.ProcessId == b.ProcessId && a.State == b.State;

    private void CheckSession()
    {
        var status = _main.Status;
        var name = _main.Profiles.FirstOrDefault(p => p.Id == status.ProfileId)?.Name ?? "No active model";
        var elapsed = status.StartedAt is { } start && status.State is ServerState.Starting or ServerState.Ready ? $" · {(DateTimeOffset.Now - start):d\\.hh\\:mm\\:ss}" : "";
        _session.Text = name + " · " + status.Message + elapsed;
        if (_observed is null || !SameSession(_observed, status))
        {
            _observed = status; _previousCpu = null;
            foreach (var value in new[] { _generation, _prompt, _requests, _tokens, _cpu, _ram }) value.Text = "—";
            _generationSession.Text = "Session average: — tokens/s";
            _slots.Children.Clear(); _errors.Text = "";
            _updated.Text = _paused ? "Paused · session changed" : "Waiting for a new snapshot…";
        }
    }

    private async Task PollAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_paused)
                {
                    CheckSession();
                    var expected = _main.Status;
                    var resourcesTask = _main.ReadResourcesAsync(expected, token);
                    var data = await _client.ReadAsync(expected.State == ServerState.Ready ? expected.BaseUrl : null, token);
                    var resources = await resourcesTask;
                    token.ThrowIfCancellationRequested();
                    if (!_paused && SameSession(expected, _main.Status))
                    {
                        double? cpu = null;
                        var timestamp = Stopwatch.GetTimestamp();
                        if (resources is { } r)
                        {
                            if (_previousCpu is { } previous)
                            {
                                var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
                                if (seconds > 0) cpu = Math.Clamp((r.CpuTime - previous.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
                            }
                            _previousCpu = (r.CpuTime, timestamp);
                        }
                        else _previousCpu = null;
                        Present(data, resources, cpu);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { _updated.Text = "Updates stopped. Close and reopen the statistics window."; }
    }

    // Presentation is independent of collection, so unavailable and populated states can be rendered in UI checks.
    public void Present(StatisticsSnapshot snapshot, ProcessResources? resources, double? cpu)
    {
        _generation.Text = N(snapshot.Metric("predicted_tokens_seconds"), "N1");
        _generationSession.Text = "Session average: " + N(snapshot.SessionGenerationTokensPerSecond, "N1") + " tokens/s";
        _prompt.Text = N(snapshot.Metric("prompt_tokens_seconds"), "N1");
        _requests.Text = N(snapshot.Metric("requests_processing")) + " / " + N(snapshot.Metric("requests_deferred"));
        _tokens.Text = N(snapshot.Metric("prompt_tokens_total")) + " / " + N(snapshot.Metric("tokens_predicted_total"));
        _cpu.Text = N(cpu, "N1") + " %";
        _ram.Text = resources is null ? "—" : N(resources.WorkingSetBytes / 1073741824d, "N2") + " GiB";
        _errors.Text = string.Join("\n", new[] { snapshot.MetricsError, snapshot.SlotsError, snapshot.GpuError }.Where(x => x is not null).Distinct());
        _errors.IsVisible = _errors.Text.Length > 0;
        _gpus.Children.Clear();
        foreach (var gpu in snapshot.Gpus)
            _gpus.Children.Add(new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 10, Children = {
                new TextBlock { Text = $"GPU {gpu.Index} · {gpu.Name}", FontWeight = FontWeight.SemiBold },
                Label($"Utilization  {N(gpu.Utilization)} %      VRAM  {N(gpu.UsedMiB)} / {N(gpu.TotalMiB)} MiB      Temperature  {N(gpu.Temperature)} °C")
            } } });
        if (snapshot.Gpus.Count == 0) _gpus.Children.Add(Label("No GPU data"));
        _slots.Children.Clear();
        var table = new Grid { ColumnDefinitions = new("90,*,*,*,*"), RowDefinitions = new("Auto") };
        string[] titles = ["Slot", "Status", "Context", "Prompt", "Generation"];
        void Cell(string text, int row, int col) { var label = Label(text); label.Margin = new Thickness(0, 5, 12, 5); Grid.SetRow(label, row); Grid.SetColumn(label, col); table.Children.Add(label); }
        for (var i = 0; i < titles.Length; i++) Cell(titles[i], 0, i);
        var row = 0;
        foreach (var slot in snapshot.Slots)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); row++;
            string[] values = [slot.Id.ToString(), slot.Processing ? "Processing" : "Idle", N(slot.Context), N(slot.PromptTokens), N(slot.GeneratedTokens)];
            for (var i = 0; i < values.Length; i++) Cell(values[i], row, i);
        }
        _slots.Children.Add(table);
        if (snapshot.Slots.Count == 0) _slots.Children.Add(Label("No slot data"));
        _updated.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss") + " · " + (resources is null ? "Process resources are unavailable" : $"Server processes: {resources.ProcessCount}");
    }
}
