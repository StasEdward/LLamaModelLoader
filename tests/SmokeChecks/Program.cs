using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Core;
using LLamaModelLoader.Desktop;
using LLamaModelLoader.Infrastructure;

if (args.Length < 2) throw new ArgumentException("Usage: SmokeChecks render OUTPUT_DIRECTORY | real SERVER_EXE MODEL_GGUF OUTPUT_DIRECTORY");
if (args[0] == "render")
{
    var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
    var session = HeadlessUnitTestSession.StartNew(typeof(UiBootstrap));
    await session.Dispatch(async () =>
    {
        var data = Path.Combine(directory, "render-data"); Directory.CreateDirectory(data);
        var model = Path.Combine(data, "Qwen3.5-9B-Q8_0.gguf"); await File.WriteAllTextAsync(model, "GGUF");
        var profile = new ModelProfile { Name = "Qwen3.5 · 9B", Description = "General-purpose model · Q8_0", ModelPath = model, Options = new() { ContextSize = 4096, GpuLayers = "auto" } };
        await new ConfigurationStore(data).SaveAsync(new() { Profiles = [profile], SelectedProfileId = profile.Id, Settings = new() { ServerPath = @"D:\llama_cpp\llama-server.exe", ModelsDirectory = data } });
        await using var vm = new MainViewModel(data);
        var window = new MainWindow(vm); window.Show();
        async Task Snapshot(string file)
        {
            await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame"); bitmap.Save(Path.Combine(directory, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        void Click(string label)
        {
            var button = window.GetVisualDescendants().OfType<Button>().First(x => x.Content?.ToString() == label);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        await Snapshot("home.png");
        var memoryData = Path.Combine(directory, "memory-data");
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../FakeLlamaServer/bin",
            AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug", "net10.0/FakeLlamaServer.exe"));
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var memoryProfile = new ModelProfile { Name = "Hybrid model · MTP", ModelPath = model, ExtraArguments = ["--test-memory"] };
        await new ConfigurationStore(memoryData).SaveAsync(new() { Profiles = [memoryProfile], SelectedProfileId = memoryProfile.Id,
            Settings = new() { ServerPath = fixture, ModelsDirectory = data, Port = port } });
        await using (var memoryVm = new MainViewModel(memoryData))
        {
            var memoryWindow = new MainWindow(memoryVm); memoryWindow.Show();
            await memoryVm.StartCommand.ExecuteAsync(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (memoryVm.Status.State == ServerState.Starting) await Task.Delay(50, timeout.Token);
            if (memoryVm.Status.State != ServerState.Ready) throw new Exception(memoryVm.Notice + memoryVm.Status.Message);
            memoryVm.Tick(); Dispatcher.UIThread.RunJobs();
            var memoryView = memoryWindow.GetVisualDescendants().OfType<MemorySummaryView>().Single();
            if (!memoryVm.ShowMemory || memoryView.Snapshot.Total.GpuMiB != 13292) throw new Exception("Memory binding failed");
            memoryVm.ClearLog();
            await Task.Delay(200); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var bitmap = memoryWindow.CaptureRenderedFrame() ?? throw new Exception("No memory frame"))
                bitmap.Save(Path.Combine(directory, "home-memory.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            // Keep the same window to exercise stale-data removal and the unavailable state.
            memoryVm.SelectedProfile!.ExtraArguments.Clear();
            await memoryVm.RestartCommand.ExecuteAsync(null);
            while (memoryVm.Status.State == ServerState.Starting) await Task.Delay(50, timeout.Token);
            memoryVm.Tick(); Dispatcher.UIThread.RunJobs();
            if (memoryVm.Memory.HasData) throw new Exception("Stale memory after restart");
            await Task.Delay(200); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var bitmap = memoryWindow.CaptureRenderedFrame() ?? throw new Exception("No unavailable memory frame"))
                bitmap.Save(Path.Combine(directory, "home-memory-unavailable.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            await memoryVm.StopCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            if (memoryVm.ShowMemory || memoryVm.Memory.HasData) throw new Exception("Memory was not cleared on stop");
            memoryWindow.Close();
        }
        Console.WriteLine("UI: memory binding, log clear, restart, unavailable state, and stop passed");
        var stats = new StatisticsWindow(vm, autoRefresh: false); stats.Show();
        async Task StatsSnapshot(string file)
        {
            await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var bitmap = stats.CaptureRenderedFrame() ?? throw new Exception("No statistics frame");
            bitmap.Save(Path.Combine(directory, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        stats.Present(new(new Dictionary<string, double>(), [], [], "Metrics are disabled. The next server start will enable --metrics.", "Slot information is unavailable.", "NVIDIA: data is unavailable."), null, null);
        await StatsSnapshot("statistics-unavailable.png");
        var sampleStatistics = new StatisticsSnapshot(new Dictionary<string, double> {
            ["llamacpp:predicted_tokens_seconds"] = 42.7, ["llamacpp:prompt_tokens_seconds"] = 1580.4,
            ["llamacpp:requests_processing"] = 1, ["llamacpp:requests_deferred"] = 0,
            ["llamacpp:prompt_tokens_total"] = 37203, ["llamacpp:tokens_predicted_total"] = 829,
            ["llamacpp:tokens_predicted_seconds_total"] = 20 },
            [new(0, true, 131072, 37203, 829)], [new("0", "NVIDIA GeForce RTX 5080", 94, 15069, 16303, 58)]);
        stats.Present(sampleStatistics, new(TimeSpan.FromSeconds(5), 9300000000, 2), 12.5);
        string? SessionRate() => stats.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "SessionGenerationRate").Text;
        var expectedRate = "Session average: " + (829d / 20).ToString("N1") + " tokens/s";
        if (SessionRate() != expectedRate) throw new Exception("Session generation rate was not displayed");
        await StatsSnapshot("statistics.png");
        var idleMetrics = new Dictionary<string, double>(sampleStatistics.Metrics) { ["llamacpp:predicted_tokens_seconds"] = 0, ["llamacpp:requests_processing"] = 0 };
        stats.Present(sampleStatistics with { Metrics = idleMetrics, Slots = [new(0, false, 131072, 37203, 0)] }, null, null);
        if (SessionRate() != expectedRate) throw new Exception("Idle polling changed the session generation rate");
        await StatsSnapshot("statistics-idle.png");
        stats.Present(new(new Dictionary<string, double>(), [], []), null, null);
        if (SessionRate() != "Session average: — tokens/s") throw new Exception("Missing session counters retained a stale rate");
        stats.Close();
        // The actual navigation opens one reusable, independently closable window.
        window.ShowStatistics(); window.ShowStatistics();
        Console.WriteLine("UI: home rendered");
        await vm.RunAsync(() => vm.DuplicateAsync(vm.SelectedProfile!));
        var secondId = vm.Profiles.Last().Id;
        await vm.RunAsync(() => vm.SelectAsync(secondId));
        if (vm.SelectedProfile?.Id != secondId) throw new Exception("Profile selection failed");
        await vm.RunAsync(() => vm.SelectAsync(profile.Id));
        Console.WriteLine("UI: profile selection persisted");
        Click("▤  Models"); await Snapshot("models.png");
        Click("Edit"); await Snapshot("editor.png");
        var contextInput = window.GetVisualDescendants().OfType<TextBox>().First(x => x.Text == "4096");
        contextInput.Text = "2048";
        var spec = window.GetVisualDescendants().OfType<Expander>().Single(x => x.Header?.ToString() == "Speculative decoding");
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = expander == spec;
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        TextBox Option(string name) => window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == name);
        Option("SpecType").Text = "draft-mtp";
        Option("SpecDraftNMax").Text = "2";
        Option("SpecDraftPMin").Text = "0.60";
        spec.BringIntoView();
        await Snapshot("speculative-editor.png");
        Click("Save profile");
        await Task.Delay(300);
        if (vm.SelectedProfile?.Options.ContextSize != 2048) throw new Exception("Editor save failed: " + vm.Notice);
        var savedSpec = new ConfigurationStore(data).Load().Value.Profiles.Single(x => x.Id == profile.Id).Options;
        if (savedSpec.SpecType != "draft-mtp" || savedSpec.SpecDraftNMax != 2 || savedSpec.SpecDraftPMin != 0.6) throw new Exception("Speculative editor save failed");
        Console.WriteLine("UI: edited context saved");
        Click("⚙  Settings"); await Snapshot("settings.png");
        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Settings")) throw new Exception("Settings navigation failed");
        window.Close();
        return true;
    }, CancellationToken.None);
    Console.WriteLine("UI pages rendered: " + directory);
    // This is a dedicated screenshot process. All application resources were disposed in Dispatch;
    // terminate the headless rendering host, which can keep its dispatcher alive on Windows.
    Environment.Exit(0);
}
else if (args[0] == "real")
{
    var directory = Path.GetFullPath(args[3]); Directory.CreateDirectory(directory);
    var settings = new AppSettings { ServerPath = Path.GetFullPath(args[1]), ModelsDirectory = Path.GetDirectoryName(Path.GetFullPath(args[2]))!, Port = 18089, StartupTimeoutSeconds = 300 };
    var profile = new ModelProfile { Name = "Qwen3.5-9B Q8_0", ModelPath = Path.GetFullPath(args[2]), Description = "Local Qwen · verified 4096-token profile", Options = new() { ContextSize = 4096, GpuLayers = "auto", Parallel = 1 } };
    await using var log = new SessionLog(directory);
    await using var controller = new ServerController(new ServerProbe(), log);
    controller.StatusChanged += state => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {state.State}: {state.Message}");
    try
    {
        await controller.StartAsync(settings, profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        while (controller.Status.State == ServerState.Starting) await Task.Delay(500, timeout.Token);
        if (controller.Status.State != ServerState.Ready) throw new Exception(controller.Status.Message);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await http.PostAsJsonAsync(controller.Status.BaseUrl + "/v1/chat/completions", new { messages = new[] { new { role = "user", content = "Reply with just OK." } }, max_tokens = 32, temperature = 0.0 });
        var body = await response.Content.ReadAsStringAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "completion.json"), body);
        response.EnsureSuccessStatusCode();
        Console.WriteLine("Chat completion HTTP " + (int)response.StatusCode);
        await controller.RestartAsync(settings, profile);
        while (controller.Status.State == ServerState.Starting) await Task.Delay(500, timeout.Token);
        if (controller.Status.State != ServerState.Ready) throw new Exception(controller.Status.Message);
        Console.WriteLine("Restart ready.");
        await controller.StopAsync();
        settings.Port = 8080;
        await new ConfigurationStore(Path.Combine(directory, "ready-config")).SaveAsync(new() { Settings = settings, Profiles = [profile], SelectedProfileId = profile.Id });
        Console.WriteLine("Real server smoke check passed; example configuration saved.");
    }
    catch { Console.Error.WriteLine(log.Snapshot()); throw; }
}
else if (args[0] == "observe")
{
    using var client = new StatisticsClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var snapshot = await client.ReadAsync(args[1], timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

public static class UiBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
