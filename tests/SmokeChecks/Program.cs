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

if (args.Length < 2) throw new ArgumentException("Usage: SmokeChecks render OUTPUT_DIRECTORY | layout OUTPUT_DIRECTORY | install OUTPUT_DIRECTORY | real SERVER_EXE MODEL_GGUF OUTPUT_DIRECTORY | metadata MODEL_GGUF | observe BASE_URL");
if (args[0] == "layout")
{
    var session = HeadlessUnitTestSession.StartNew(typeof(UiBootstrap));
    await session.Dispatch(async () => { await ScrollLayoutChecks.RunAsync(Path.GetFullPath(args[1])); return true; }, CancellationToken.None);
}
else if (args[0] == "render")
{
    var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
    var session = HeadlessUnitTestSession.StartNew(typeof(UiBootstrap));
    await session.Dispatch(async () =>
    {
        var data = Path.Combine(directory, "render-data"); Directory.CreateDirectory(data);
        var model = Path.Combine(data, "Qwen3.5-9B-Q8_0.gguf"); WritePreviewModel(model);
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
        var logPanel = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString() == "Server log");
        await vm.RunAsync(() => throw new InvalidOperationException(string.Join('\n', Enumerable.Range(1, 100).Select(i => $"Log fixture line {i}"))));
        vm.Tick(); logPanel.IsExpanded = true;
        await Task.Delay(200); Dispatcher.UIThread.RunJobs();
        var logBox = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "ServerLogText");
        var logScroll = logBox.GetVisualDescendants().OfType<ScrollViewer>().First();
        void AssertLogAtEnd()
        {
            if (logScroll.Extent.Height <= logScroll.Viewport.Height || Math.Abs(logScroll.Offset.Y - (logScroll.Extent.Height - logScroll.Viewport.Height)) > 1)
                throw new Exception("Server log did not scroll to its last line");
        }
        AssertLogAtEnd();
        logScroll.Offset = default;
        await vm.RunAsync(() => throw new InvalidOperationException("New log entry")); vm.Tick();
        await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AssertLogAtEnd();
        logPanel.IsExpanded = false; logScroll.Offset = default; logPanel.IsExpanded = true;
        await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AssertLogAtEnd();
        logPanel.IsExpanded = false; vm.ClearLog(); vm.Notice = "";
        Console.WriteLine("UI: server log follows the last line on expand, append, and reopen");
        var metadataExpander = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString() == "GGUF metadata");
        metadataExpander.IsExpanded = true;
        await Task.Delay(700); Dispatcher.UIThread.RunJobs();
        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Training context: 262,144") == true)) throw new Exception("GGUF metadata was not displayed");
        metadataExpander.BringIntoView(); await Snapshot("gguf-metadata.png"); metadataExpander.IsExpanded = false;
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
            var optimizer = new OptimizationWindow(memoryVm, memoryVm.SelectedProfile!); optimizer.Show();
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            TextBox OptimizationInput(string name) => optimizer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == name);
            OptimizationInput("OptimizationReserve").Text = "0";
            OptimizationInput("OptimizationContext").Text = "4096";
            OptimizationInput("OptimizationRepetitions").Text = "1";
            OptimizationInput("OptimizationTokens").Text = "16";
            optimizer.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "OptimizationGpu").IsChecked = false;
            Button OptimizationButton(string text) => optimizer.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == text);
            OptimizationButton("Preview candidates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var optimizationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!OptimizationButton("Run benchmark").IsEnabled) await Task.Delay(50, optimizationTimeout.Token);
            async Task OptimizationSnapshot(string file)
            {
                await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                using var bitmap = optimizer.CaptureRenderedFrame() ?? throw new Exception("No optimization frame");
                bitmap.Save(Path.Combine(directory, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            await OptimizationSnapshot("optimization-setup.png");
            if (!OptimizationButton("Run benchmark").IsEnabled) throw new Exception("An unchanged preview was invalidated by delayed UI events");
            OptimizationButton("Run benchmark").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!memoryVm.IsOptimizing || memoryVm.CanStop || memoryVm.CanStart || memoryVm.CanRestart)
                throw new Exception($"Optimization did not lock server controls: optimizing={memoryVm.IsOptimizing}, start={memoryVm.CanStart}, stop={memoryVm.CanStop}, restart={memoryVm.CanRestart}; " +
                    optimizer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "OptimizationStatus").Text);
            try { await memoryVm.SaveProfileAsync(memoryVm.SelectedProfile!); throw new Exception("Profile edited during optimization"); }
            catch (InvalidOperationException) { }
            while (memoryVm.IsOptimizing) await Task.Delay(50, optimizationTimeout.Token);
            await Task.Delay(200); Dispatcher.UIThread.RunJobs();
            var saveResult = OptimizationButton("Save as new profile");
            if (!saveResult.IsEnabled) throw new Exception("No eligible benchmark result");
            var countBeforeSave = memoryVm.Profiles.Count;
            saveResult.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (memoryVm.Profiles.Count == countBeforeSave) await Task.Delay(50, optimizationTimeout.Token);
            if (memoryVm.Profiles[0].Options.ContextSize is not null || memoryVm.Profiles.Last().Options.ContextSize != 4096 || memoryVm.Status.State != ServerState.Stopped)
                throw new Exception("Saving benchmark result changed original settings or server state");
            optimizer.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "OptimizationResults").BringIntoView();
            await OptimizationSnapshot("optimization-results.png");
            optimizer.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "OptimizationMode").SelectedIndex = 1;
            if (OptimizationButton("Run benchmark").IsEnabled) throw new Exception("A changed mode retained stale candidates");
            OptimizationButton("Preview candidates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!OptimizationButton("Run benchmark").IsEnabled) await Task.Delay(50, optimizationTimeout.Token);
            if (!optimizer.GetVisualDescendants().OfType<Expander>().Any(e => e.Header?.ToString() == memoryProfile.Name))
                throw new Exception("Saved-profile comparison preview did not use the selected profile");
            optimizer.Close();
            Console.WriteLine("UI: optimization preview, run, exclusive controls, results, and save as new profile passed");
            await ChatSmokeChecks.RunAsync(memoryWindow, memoryVm, directory);
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
        string? GpuSummary(string name) => stats.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == name).Text;
        var expectedGpuMemory = $"{15069d / 1024:N2} GiB";
        if (GpuSummary("GpuUtilizationSummary") != "94 %" || GpuSummary("GpuMemorySummary") != expectedGpuMemory ||
            !stats.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == $"of {16303d / 1024:N2} GiB · GPU 0 · whole device"))
            throw new Exception("GPU summaries were not displayed alongside CPU/RAM");
        string? SessionRate() => stats.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "SessionGenerationRate").Text;
        var expectedRate = "Session average: " + (829d / 20).ToString("N1") + " tokens/s";
        if (SessionRate() != expectedRate) throw new Exception("Session generation rate was not displayed");
        await StatsSnapshot("statistics.png");
        var idleMetrics = new Dictionary<string, double>(sampleStatistics.Metrics) { ["llamacpp:predicted_tokens_seconds"] = 0, ["llamacpp:requests_processing"] = 0 };
        stats.Present(sampleStatistics with { Metrics = idleMetrics, Slots = [new(0, false, 131072, 37203, 0)] }, null, null);
        if (SessionRate() != expectedRate) throw new Exception("Idle polling changed the session generation rate");
        await StatsSnapshot("statistics-idle.png");
        stats.Present(sampleStatistics with { Gpus = [new("0", "First GPU", 0, 1024, 8192, 40), new("1", "Second GPU", null, null, 16384, null)] }, null, null);
        if (GpuSummary("GpuUtilizationSummary") != "GPU 0: 0 %\nGPU 1: — %" ||
            GpuSummary("GpuMemorySummary") != $"GPU 0: {1d:N2} / {8d:N2} GiB\nGPU 1: — / {16d:N2} GiB")
            throw new Exception("GPU summaries lost individual devices or unavailable values");
        stats.Width = 780;
        await StatsSnapshot("statistics-multiple-gpus.png");
        stats.Width = 1040;
        stats.Present(new(new Dictionary<string, double>(), [], []), null, null);
        if (SessionRate() != "Session average: — tokens/s") throw new Exception("Missing session counters retained a stale rate");
        if (GpuSummary("GpuUtilizationSummary") != "— %" || GpuSummary("GpuMemorySummary") != "— GiB")
            throw new Exception("Missing GPU data retained stale summaries");
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
        var chat = window.GetVisualDescendants().OfType<Expander>().Single(x => x.Header?.ToString() == "Chat");
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = expander == chat;
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        if (Option("ReasoningBudget").Text != "8192" || Option("ReasoningBudgetMessage").Text != "Enough thinking. Act now: make the next tool call.")
            throw new Exception("Reasoning defaults were not displayed");
        Option("ReasoningBudget").Text = "4096";
        Option("ReasoningBudgetMessage").Text = "Enough thinking. Act now: make the next tool call.";
        chat.BringIntoView(); await Snapshot("reasoning-editor.png");
        Click("Save profile");
        await Task.Delay(300);
        if (vm.SelectedProfile?.Options.ContextSize != 2048) throw new Exception("Editor save failed: " + vm.Notice);
        var savedSpec = new ConfigurationStore(data).Load().Value.Profiles.Single(x => x.Id == profile.Id).Options;
        if (savedSpec.SpecType != "draft-mtp" || savedSpec.SpecDraftNMax != 2 || savedSpec.SpecDraftPMin != 0.6) throw new Exception("Speculative editor save failed");
        if (savedSpec.ReasoningBudget != 4096 || savedSpec.ReasoningBudgetMessage != "Enough thinking. Act now: make the next tool call.")
            throw new Exception("Reasoning editor save failed");
        Console.WriteLine("UI: edited context saved");
        Click("⚙  Settings"); await Snapshot("settings.png");
        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Settings")) throw new Exception("Settings navigation failed");
        await InstallationSmokeChecks.RunAsync(window, directory, fixture);
        await ProfileTransferSmokeChecks.RunAsync(window, vm, directory);
        window.Close();
        return true;
    }, CancellationToken.None);
    Console.WriteLine("UI pages rendered: " + directory);
    // This is a dedicated screenshot process. All application resources were disposed in Dispatch;
    // terminate the headless rendering host, which can keep its dispatcher alive on Windows.
    Environment.Exit(0);
}
else if (args[0] == "install")
{
    // Explicit opt-in: downloads and probes an official CPU build, without loading a model or editing Settings.
    var directory = Path.GetFullPath(args[1]);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var builds = await new ServerReleaseClient(http).GetBuildsAsync(timeout.Token);
    var build = builds.First(b => b.Backend == "cpu");
    var installer = new ServerInstaller(directory, http);
    var installed = await installer.InstallAsync(build, new Progress<InstallationProgress>(p => Console.WriteLine(p.Message)), timeout.Token);
    Console.WriteLine(installed.Version);
    Console.WriteLine("Verified official CPU installation: " + installed.ExecutablePath);
    if (!installer.GetInstalled().Any(i => i.ExecutablePath == installed.ExecutablePath)) throw new Exception("Installation was not persisted");
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
else if (args[0] == "metadata")
{
    var metadata = await GgufReader.ReadAsync(Path.GetFullPath(args[1]));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(metadata with { ChatTemplate = metadata.ChatTemplate is null ? null : $"Present ({metadata.ChatTemplate.Length} characters)" },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
else if (args[0] == "observe")
{
    using var client = new StatisticsClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var snapshot = await client.ReadAsync(args[1], timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

static void WritePreviewModel(string path)
{
    using var writer = new BinaryWriter(File.Create(path), System.Text.Encoding.UTF8);
    void Text(string text) { var bytes = System.Text.Encoding.UTF8.GetBytes(text); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
    void StringValue(string key, string value) { Text(key); writer.Write(8u); Text(value); }
    void Integer(string key, uint value) { Text(key); writer.Write(4u); writer.Write(value); }
    writer.Write("GGUF"u8); writer.Write(3u); writer.Write(2UL); writer.Write(6UL);
    StringValue("general.name", "GGUF metadata demo"); StringValue("general.architecture", "qwen35");
    Integer("qwen35.context_length", 262144); Integer("qwen35.block_count", 32);
    StringValue("tokenizer.ggml.model", "gpt2"); StringValue("tokenizer.chat_template", "{{ messages }}");
    Text("weight"); writer.Write(2u); writer.Write(4096UL); writer.Write(4096UL); writer.Write(8u); writer.Write(0UL);
    Text("bias"); writer.Write(1u); writer.Write(4096UL); writer.Write(0u); writer.Write(0UL);
}

public static class UiBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
