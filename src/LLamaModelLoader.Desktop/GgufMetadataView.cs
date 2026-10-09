using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

public sealed class GgufMetadataView : ContentControl
{
    public static readonly StyledProperty<string?> ModelPathProperty = AvaloniaProperty.Register<GgufMetadataView, string?>(nameof(ModelPath));
    public string? ModelPath { get => GetValue(ModelPathProperty); set => SetValue(ModelPathProperty, value); }
    private CancellationTokenSource? _read;
    public GgufMetadataView()
    {
        AttachedToVisualTree += (_, _) => Reload();
        DetachedFromVisualTree += (_, _) => { _read?.Cancel(); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelPathProperty) Reload();
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private async void Reload()
    {
        _read?.Cancel(); _read?.Dispose();
        var cancellation = _read = new CancellationTokenSource(); var token = cancellation.Token;
        if (string.IsNullOrWhiteSpace(ModelPath)) { Content = Text("Select a GGUF file to inspect its metadata."); return; }
        var path = ModelPath;
        Content = Text("Reading GGUF metadata…");
        try
        {
            await Task.Delay(250, token);
            var data = await GgufReader.ReadAsync(path, token);
            token.ThrowIfCancellationRequested();
            var culture = CultureInfo.InvariantCulture;
            string Number(ulong? value) => value?.ToString("N0", culture) ?? "Unknown";
            var types = string.Join(", ", data.TensorTypes.OrderByDescending(t => t.Value).Select(t => $"{t.Key}: {t.Value:N0}"));
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Text($"{data.Name ?? "Unnamed model"} · {data.Architecture ?? "Unknown architecture"}"));
            panel.Children.Add(Text($"Stored parameters: {Number(data.StoredParameters)}" + (data.SizeLabel is { } size ? $" · Size label: {size}" : "")));
            panel.Children.Add(Text($"Training context: {Number(data.ContextLength)} · Layers: {Number(data.Layers)}"));
            panel.Children.Add(Text($"Tokenizer: {data.Tokenizer ?? "Unknown"} · Vocabulary: {Number(data.VocabularySize)}"));
            panel.Children.Add(Text($"GGUF v{data.Version} · {data.Parts} file(s) · {data.FileBytes / 1073741824d:F2} GiB · {data.TensorCount:N0} tensors"));
            panel.Children.Add(Text("Tensor types: " + (types.Length > 0 ? types : "None")));
            panel.Children.Add(new Expander { Header = data.ChatTemplate is null ? "Chat template: not available" : "Chat template",
                IsEnabled = data.ChatTemplate is not null, Content = new TextBox { Text = data.ChatTemplate, IsReadOnly = true,
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200, FontSize = 11 } });
            Content = panel;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        { if (!token.IsCancellationRequested) Content = Text("Metadata unavailable: " + ex.Message); }
    }
}
