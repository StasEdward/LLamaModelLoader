using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Desktop;

public static class InstallationSmokeChecks
{
    public static async Task RunAsync(Window owner, string directory, string fixture)
    {
        var data = Path.Combine(directory, "installer-data-" + Guid.NewGuid().ToString("N"));
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, true))
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fixture)!))
                archive.CreateEntryFromFile(file, file == fixture ? "llama-server.exe" : Path.GetFileName(file));
        var bytes = archiveBytes.ToArray();
        const string name = "llama-b100-bin-win-cpu-x64.zip";
        var json = JsonSerializer.Serialize(new[] { new { tag_name = "b100", draft = false, prerelease = true,
            published_at = "2026-10-10T00:00:00Z", assets = new[] { new { name,
                browser_download_url = "https://github.com/ggml-org/llama.cpp/releases/download/b100/" + name,
                size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) } } } });
        using var http = new HttpClient(new FixtureHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(json) : new ByteArrayContent(bytes) })));
        var manager = new ServerInstallationsWindow(data, @"D:\llama_cpp\llama-server.exe", http);
        var dialog = manager.ShowDialog<string?>(owner);
        Button FindButton(string label) => manager.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label);
        string? Status() => manager.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "InstallationStatus").Text;
        async Task WaitAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!condition())
            {
                if (Status()?.StartsWith("Operation failed") == true) throw new Exception(Status());
                await Task.Delay(50, timeout.Token);
            }
        }
        async Task SnapshotAsync(string name)
        {
            await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var bitmap = manager.CaptureRenderedFrame() ?? throw new Exception("No installer frame");
            bitmap.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        FindButton("Check for updates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => FindButton("Install selected build").IsEnabled);
        await SnapshotAsync("installer-available.png");
        FindButton("Install selected build").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => FindButton("Use selected build").IsEnabled);
        if (FindButton("Install selected build").IsEnabled) throw new Exception("An installed build can be installed again");
        await SnapshotAsync("installer-installed.png");
        FindButton("Use selected build").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var selected = await dialog;
        if (selected is null || !File.Exists(selected) || !selected.StartsWith(data, StringComparison.OrdinalIgnoreCase)) throw new Exception("Installer returned an invalid path");
        if (File.Exists(Path.Combine(data, "config.json"))) throw new Exception("Installer unexpectedly saved Settings");
        var reopened = new ServerInstallationsWindow(data, selected, http); reopened.Show();
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        if (!reopened.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Use selected build").IsEnabled)
            throw new Exception("An existing managed installation was not restored");
        reopened.Close();
        using var blockedHttp = new HttpClient(new FixtureHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new Exception(); }));
        var cancelWindow = new ServerInstallationsWindow(data, selected, blockedHttp); cancelWindow.Show();
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        cancelWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Check for updates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        cancelWindow.Close(); await cancelWindow.CancelAndWaitAsync();
        if (cancelWindow.IsVisible) throw new Exception("Installer did not cancel and close");
        Console.WriteLine("UI: release check, verified install, executable probe, selection, persisted installations, and cancel-on-close passed");
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
