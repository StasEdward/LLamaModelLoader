using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Core;
using LLamaModelLoader.Desktop;
using LLamaModelLoader.Infrastructure;

public static class DiscoverSmokeChecks
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const long GiB = 1L << 30;

    public static async Task RunAsync(string directory)
    {
        var data = Path.Combine(directory, "discover-data-" + Guid.NewGuid().ToString("N"));
        var models = Path.Combine(data, "models"); Directory.CreateDirectory(models);
        var server = Path.Combine(data, "llama-server.exe"); await File.WriteAllBytesAsync(server, []);
        await new ConfigurationStore(data).SaveAsync(new() { Settings = new() { ServerPath = server, ModelsDirectory = "" } });
        var fixture = new Fixture(Header());
        ModelProfile? created = null;
        await using var vm = new MainViewModel(data, fixture);
        var window = new MainWindow(vm); window.Show();
        var host = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PageHost");
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "⇩  Discover"));
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        if (host.Content is not DiscoverView) throw new Exception("Discover navigation failed");
        DiscoverView NewView() => new(vm, p => created = p,
            _ => Task.FromResult<(GpuMemory?, string?)>((new GpuMemory("NVIDIA GeForce RTX 5080", 16303L << 20, 2128L << 20), null)), () => 64 * GiB);
        host.Content = NewView();
        DiscoverView View() => (DiscoverView)host.Content!;
        T Named<T>(string name) where T : Control => View().GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        T[] All<T>() where T : Control => View().GetVisualDescendants().OfType<T>().ToArray();
        string? Status() => Named<TextBlock>("DiscoverStatus").Text;
        string? Badge(string label) => All<TextBlock>().SingleOrDefault(t => t.Name == "Badge" + label)?.Text;
        string? Breakdown(string label) => All<TextBlock>().SingleOrDefault(t => t.Name == "Breakdown" + label)?.Text;
        Button? Download(string label) => All<Button>().SingleOrDefault(b => b.Name == "Download" + label);
        ListBox Results() => Named<ListBox>("DiscoverResults");
        async Task WaitAsync(Func<bool> condition, string what)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { while (true) { Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(30, timeout.Token); } }
            catch (OperationCanceledException) { throw new Exception($"Discover check timed out waiting for {what}; status: {Status()}"); }
        }
        async Task SearchAsync()
        {
            await WaitAsync(() => All<TextBox>().Any(t => t.Name == "DiscoverSearch"), "the page");
            Named<TextBox>("DiscoverSearch").Text = "r";
            Click(All<Button>().Single(b => b.Content?.ToString() == "Search"));
            await WaitAsync(() => Results().ItemCount == 2 && All<TextBlock>().Any(t => t.Text == "o/r") && All<TextBlock>().Any(t => t.Text == "g/gated"), "search results");
        }
        async Task SelectAsync(string id)
        {
            Results().SelectedItem = Results().Items.Cast<HfModelSummary>().Single(m => m.Id == id);
            await WaitAsync(() => All<TextBlock>().Any(t => t.Name == "DiscoverTitle" && t.Text == id) && Badge("Q4_K_M") is not null &&
                (id != "o/r" || Badge("Q4_K_M") == "Fits in VRAM"), "details of " + id);
        }

        await SearchAsync();
        if (!All<TextBlock>().Any(t => t.Text?.StartsWith("No downloads yet.") == true)) throw new Exception("The empty download queue shows no placeholder");
        await SelectAsync("g/gated");
        if (All<Button>().Any(b => b.Content?.ToString() is "Download" or "Resume" or "Downloaded") || !All<Button>().Any(b => b.Content?.ToString() == "Open on Hugging Face"))
            throw new Exception("A gated repository offered downloads or no Hugging Face link");

        await SelectAsync("o/r");
        if (Badge("Q8_0") != "Partial offload · 3/4 layers" || Badge("F16") != "Does not fit")
            throw new Exception($"Unexpected fit badges: {Badge("Q4_K_M")} / {Badge("Q8_0")} / {Badge("F16")}");
        var gpuLine = $"NVIDIA GeForce RTX 5080 · {16303d / 1024:N2} GiB ({2128d / 1024:N2} GiB in use)";
        if (!All<TextBlock>().Any(t => t.Text == gpuLine)) throw new Exception("GPU line missing: " + gpuLine);
        foreach (var label in new[] { "Q2_K", "Q4_K_M", "Q8_0", "F16" })
            if (Download(label) is not { IsEnabled: false }) throw new Exception("Download is enabled without a models folder: " + label);
        if (Status() != "Set a models folder in Settings first.") throw new Exception("Missing models folder message: " + Status());

        var requests = fixture.Requests; var before = Breakdown("Q8_0");
        Named<ComboBox>("DiscoverContext").SelectedItem = 32768;
        await WaitAsync(() => Breakdown("Q8_0") != before, "breakdown after a context change");
        if (fixture.Requests != requests) throw new Exception("Changing the context made an HTTP request");
        Named<ComboBox>("DiscoverContext").SelectedItem = 16384;
        Named<TextBox>("DiscoverManualVram").Text = "8";
        await WaitAsync(() => Badge("Q8_0") == "Partial offload · 1/4 layers", "manual VRAM badge");
        Named<TextBox>("DiscoverManualVram").Text = "";
        await WaitAsync(() => Badge("Q8_0") == "Partial offload · 3/4 layers", "detected VRAM badge");

        fixture.Mode = "429";
        Click(All<Button>().Single(b => b.Content?.ToString() == "Search"));
        await WaitAsync(() => Status() == "Hugging Face refused the request (rate limit). Try again later.", "rate-limit message");
        if (Results().ItemCount != 2) throw new Exception("A rate-limited search removed the listed results");
        fixture.Mode = "throw";
        Click(All<Button>().Single(b => b.Content?.ToString() == "Search"));
        await WaitAsync(() => Status() == "Hugging Face is unavailable. Check the connection and try again.", "unavailable message");
        if (Results().ItemCount != 2) throw new Exception("A failed search removed the listed results");
        fixture.Mode = "ok";

        var settings = vm.Configuration.Clone().Settings; settings.ModelsDirectory = models;
        await vm.SaveSettingsAsync(settings);
        host.Content = NewView();
        await SearchAsync();
        await SelectAsync("o/r");
        if (Download("Q2_K") is not { IsEnabled: true } download || download.Content?.ToString() != "Download") throw new Exception("Download is unavailable with a models folder");
        Click(download);
        await WaitAsync(() => vm.Downloads.Items.Any(i => i.State == DownloadItemState.Completed) && Download("Q2_K") is { IsEnabled: false } done && done.Content?.ToString() == "Downloaded",
            "completed download");
        var final = Path.Combine(models, "o", "r", "r-Q2_K.gguf");
        if (!File.Exists(final) || !(await File.ReadAllBytesAsync(final)).SequenceEqual(fixture.Body)) throw new Exception("The downloaded file is missing or different");
        await Task.Delay(200); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using (var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No discover frame"))
            bitmap.Save(Path.Combine(directory, "discover.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var queue = Named<StackPanel>("DiscoverQueue");
        Click(queue.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Create profile"));
        if (created is not { Name: "r-Q2_K", Options: { ContextSize: 16384, GpuLayers: "all", CacheTypeK: null } } || created.ModelPath != final)
            throw new Exception("Create profile passed unexpected values");

        var quant = new HfQuantization("Q4_K_M", [new HfFile("x-Q4_K_M.gguf", 1, new string('a', 64))]);
        var partial = DiscoverView.ProfileFor("o/r", quant, final, 8192, "q8_0", new FitResult(FitLevel.Partial, 1, 1, 1, 1, 4, 3, true, false));
        if (partial.Options.GpuLayers != "3" || partial.Options.CacheTypeK != "q8_0" || partial.Options.CacheTypeV != "q8_0" || partial.Options.ContextSize != 8192)
            throw new Exception("ProfileFor ignored a partial fit or the KV type");
        if (DiscoverView.ProfileFor("o/a,b", quant, final, 4096, "f16", null) is not { Name: "ab-Q4_K_M", Options: { GpuLayers: null, CacheTypeK: null } })
            throw new Exception("ProfileFor kept a comma or set options without a fit");
        window.Close();
        Console.WriteLine("UI: discover search, fit badges, download and create profile passed");
    }

    // A synthetic GGUF header (4 layers, 8 KV heads, 128/128, three tokens, no tensors), zero-padded so it is also a complete downloadable file.
    private static byte[] Header()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
            void Integer(string key, uint value) { Text(key); writer.Write(4u); writer.Write(value); }
            writer.Write("GGUF"u8); writer.Write(3u); writer.Write(0UL); writer.Write(8UL);
            Text("general.architecture"); writer.Write(8u); Text("llama");
            Integer("llama.block_count", 4); Integer("llama.attention.head_count", 32); Integer("llama.attention.head_count_kv", 8);
            Integer("llama.attention.key_length", 128); Integer("llama.attention.value_length", 128); Integer("llama.embedding_length", 4096);
            Text("tokenizer.ggml.tokens"); writer.Write(9u); writer.Write(8u); writer.Write(3UL); Text("a"); Text("b"); Text("c");
        }
        var body = new byte[65536]; stream.ToArray().CopyTo(body, 0);
        return body;
    }

    private sealed class Fixture(byte[] body) : HttpMessageHandler
    {
        private int _requests;
        public byte[] Body => body;
        public string Mode { get; set; } = "ok";
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host != "huggingface.co") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path == "/api/models")
            {
                if (Mode == "throw") throw new HttpRequestException("No such host is known.");
                if (Mode == "429") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                return Json("""
                    [{"id":"o/r","author":"o","downloads":125000,"likes":420,"lastModified":"2026-10-01T00:00:00.000Z","tags":["gguf"]},
                     {"id":"g/gated","author":"g","downloads":9000,"likes":31,"lastModified":"2026-09-15T00:00:00.000Z","gated":"auto","pipeline_tag":"image-text-to-text","tags":["gguf"]}]
                    """);
            }
            if (path == "/api/models/o/r")
                return Json($$$"""
                    {"id":"o/r","sha":"{{{Sha}}}","gated":false,"tags":["gguf","license:apache-2.0"],
                     "gguf":{"architecture":"llama","total":8030261248,"context_length":32768},
                     "siblings":[
                      {"rfilename":"README.md","size":1200},
                      {"rfilename":"r-Q2_K.gguf","size":65536,"lfs":{"sha256":"{{{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}}}","size":65536}},
                      {"rfilename":"r-Q4_K_M.gguf","size":{{{2 * GiB}}},"lfs":{"sha256":"{{{new string('1', 64)}}}"}},
                      {"rfilename":"r-Q8_0.gguf","size":{{{15 * GiB}}},"lfs":{"sha256":"{{{new string('2', 64)}}}"}},
                      {"rfilename":"r-F16.gguf","size":{{{200 * GiB}}},"lfs":{"sha256":"{{{new string('3', 64)}}}"}}]}
                    """);
            if (path == "/api/models/g/gated")
                return Json($$$"""
                    {"id":"g/gated","sha":"{{{Sha}}}","gated":"auto","tags":["gguf"],"gguf":{"architecture":"gemma3","context_length":131072},
                     "siblings":[{"rfilename":"gated-Q4_K_M.gguf","size":{{{3 * GiB}}},"lfs":{"sha256":"{{{new string('4', 64)}}}"}}]}
                    """);
            if (path == $"/o/r/resolve/{Sha}/r-Q2_K.gguf")
            {
                if (request.Headers.Range?.Ranges.Single() is { From: { } from } range)
                {
                    var to = Math.Min(range.To ?? body.Length - 1, body.Length - 1);
                    var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body, (int)from, (int)(to - from + 1)) };
                    partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
                    return Task.FromResult(partial);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
