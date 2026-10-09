using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Core;
using LLamaModelLoader.Desktop;
using LLamaModelLoader.Infrastructure;

internal static class ScrollLayoutChecks
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var data = Path.Combine(directory, "data");
        var profiles = Enumerable.Range(1, 8).Select(i => new ModelProfile { Name = $"Model profile {i}", ModelPath = @"D:\Models\example.gguf" }).ToList();
        await new ConfigurationStore(data).SaveAsync(new() { Profiles = profiles, SelectedProfileId = profiles[0].Id });
        await using var vm = new MainViewModel(data);
        var window = new MainWindow(vm); window.Show();
        var errors = new List<string>();
        async Task Check(Window owner, ScrollViewer viewer, string name)
        {
            await Task.Delay(100); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().First(x => x.Orientation == Orientation.Vertical && x.TemplatedParent == viewer);
            var content = (Control)viewer.Content!;
            // Measure the actual page content, including its right margin, against the scrollbar hit area.
            var point = content.TranslatePoint(new Point(content.Bounds.Width, 0), viewer)!.Value;
            var barLeft = bar.IsVisible ? bar.TranslatePoint(default, viewer)!.Value.X : viewer.Bounds.Width;
            var gap = barLeft - point.X;
            Console.WriteLine($"{name}: content-to-scrollbar gap = {gap:0.0} px");
            if (gap < 8) errors.Add($"{name}: scrollbar overlaps or crowds content ({gap:0.0} px)");
            using var bitmap = owner.CaptureRenderedFrame() ?? throw new Exception("No layout frame");
            bitmap.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        foreach (var width in new[] { 1180, 900 })
        {
            window.Width = width; window.Height = 640;
            foreach (var page in new[] { "Home", "Models", "Settings" })
            {
                window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString()?.EndsWith("  " + page) == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var host = window.FindControl<ContentControl>("PageHost")!;
                await Check(window, (ScrollViewer)host.Content!, $"{page.ToLowerInvariant()}-{width}");
            }
            window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString()?.EndsWith("  Models") == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = (DockPanel)window.FindControl<ContentControl>("PageHost")!.Content!;
            await Check(window, editor.Children.OfType<ScrollViewer>().Single(), $"editor-{width}");
        }
        var stats = new StatisticsWindow(vm, autoRefresh: false) { Height = 600 }; stats.Show();
        foreach (var width in new[] { 1040, 780 })
        {
            stats.Width = width;
            await Check(stats, (ScrollViewer)stats.Content!, $"statistics-{width}");
        }
        stats.Close(); window.Close();
        if (errors.Count > 0) throw new Exception(string.Join(Environment.NewLine, errors));
        Console.WriteLine("UI: page content stays clear of vertical scrollbars at normal and minimum widths");
    }
}
