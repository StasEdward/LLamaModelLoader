using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Desktop;

public sealed class MemorySummaryView : ContentControl
{
    public static readonly StyledProperty<MemoryBreakdown> SnapshotProperty =
        AvaloniaProperty.Register<MemorySummaryView, MemoryBreakdown>(nameof(Snapshot), MemoryBreakdown.Empty);
    public MemoryBreakdown Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    public MemorySummaryView() => Present();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SnapshotProperty) Present();
    }

    private void Present()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Loaded model memory", FontSize = 18, FontWeight = FontWeight.SemiBold });
        if (!Snapshot.HasData)
        {
            panel.Children.Add(Note("Memory details are unavailable in this server log. Use log verbosity 4 or higher and reload the model."));
            Content = panel;
            return;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,150") };
        var row = 0;
        void Add(string label, string gpu, string host, bool bold = false)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (value, column) in new[] { (label, 0), (gpu, 1), (host, 2) })
            {
                var text = new TextBlock { Text = value, Margin = new Thickness(0, 4, 0, 4),
                    TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                    HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
                Grid.SetRow(text, row); Grid.SetColumn(text, column); grid.Children.Add(text);
            }
            row++;
        }
        void Amount(string label, MemoryAmount amount) => Add(label, Format(amount.GpuMiB), Format(amount.HostMiB));
        Add("Buffer", "GPU", "CPU / host", true);
        Amount("Model weights", Snapshot.Weights);
        Amount("Main KV cache", Snapshot.MainKvCache);
        Amount("Recurrent state", Snapshot.RecurrentState);
        Amount("MTP KV cache", Snapshot.MtpKvCache);
        Amount("Compute buffers", Snapshot.Compute);
        if (Snapshot.Output.GpuMiB.HasValue || Snapshot.Output.HostMiB.HasValue) Amount("Output buffers", Snapshot.Output);
        Add("Reported total", Format(Snapshot.Total.GpuMiB, true), Format(Snapshot.Total.HostMiB, true), true);
        panel.Children.Add(grid);
        panel.Children.Add(Note("Sum of buffers reported during this load, including MTP. — means not reported. Driver overhead and other GPU allocations are excluded; host mappings are not resident RAM usage."));
        Content = panel;
    }

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private static string Format(double? mib, bool total = false) => mib is { } value
        ? total ? "≈ " + (value / 1024).ToString("N2", CultureInfo.InvariantCulture) + " GiB"
            : value.ToString("N1", CultureInfo.InvariantCulture) + " MiB"
        : "—";
}
