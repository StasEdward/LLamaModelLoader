using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LLamaModelLoader.Core;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

/// <summary>One in-memory conversation, owned by the main window rather than its current page.</summary>
public sealed class ChatView : UserControl, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly ChatClient _client = new();
    private readonly List<ChatMessage> _history = [];
    private readonly StackPanel _messages = new() { Spacing = 12 };
    private readonly TextBlock _model = Label(""), _status = Label("Start a model on Home to chat.");
    private readonly TextBlock _empty = Label("Send a message to test the running model. Conversation history stays in memory until New chat or application exit.");
    private readonly TextBox _input = new() { Name = "ChatInput", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 85, MaxHeight = 160,
        PlaceholderText = "Message the model…  Ctrl+Enter to send; Enter for a new line." };
    private readonly TextBox _system = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, MaxHeight = 140, PlaceholderText = "Optional system instructions" };
    private readonly TextBox _limit = new() { Text = "16384", Width = 130, Name = "ChatTokenLimit" };
    private readonly Button _send = new() { Content = "Send", Name = "ChatSend", Classes = { "primary" } },
        _stop = new() { Content = "Stop response", Name = "ChatStop", IsEnabled = false },
        _clear = new() { Content = "New chat", Name = "ChatClear" };
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private ChatTarget? _conversationTarget;
    private Reply? _reply;
    private bool _disposed, _clearing;
    private string? _cancelReason;

    private sealed class Reply
    {
        public StringBuilder Content { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public SelectableTextBlock Answer { get; } = Selectable("Waiting for the model…");
        public SelectableTextBlock Thinking { get; } = Selectable("");
        public Expander ThinkingPanel { get; } = new() { Header = "Thinking", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        public TextBlock Status { get; } = Label("");
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
        public int? PromptTokens, CompletionTokens;
        public string? FinishReason;
        public bool Dirty;
    }

    public ChatView(MainViewModel main)
    {
        _main = main; _status.Name = "ChatStatus"; _model.Name = "ChatModel";
        _messages.Children.Add(_empty);
        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var options = new Expander { Header = "Chat options", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Stack(Label("System instructions"), _system, Row(Label("Maximum output tokens"), _limit),
                Label("The output limit includes thinking. Sampling and reasoning settings come from the running server profile.")) };
        var heading = new TextBlock { Text = "Chat", Classes = { "title" } };
        var header = Stack(heading, _model, options);
        var footer = Stack(_status, _input, Row(_send, _stop, _clear));
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto") };
        header.Margin = new Thickness(0, 0, 14, 14); footer.Margin = new Thickness(0, 14, 0, 0);
        grid.Children.Add(header); Grid.SetRow(_scroll, 1); grid.Children.Add(_scroll); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid;
        _send.Click += (_, _) => BeginSend();
        _stop.Click += (_, _) => Cancel("Stopped by you. Partial response is excluded from later requests.");
        _clear.Click += async (_, _) =>
        {
            _clearing = true; UpdateState();
            try
            {
                await CancelAndWaitAsync();
                _history.Clear(); _messages.Children.Clear(); _messages.Children.Add(_empty); _conversationTarget = null;
                _input.Text = ""; _status.Text = "New chat. Enter a message.";
            }
            finally { _clearing = false; UpdateState(); }
        };
        _input.TextChanged += (_, _) => UpdateState();
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; BeginSend(); } };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => { UpdateState(); RenderReply(); });
        _timer.Start(); UpdateState();
    }

    private void UpdateState()
    {
        if (_disposed) return;
        var target = _main.GetChatTarget();
        var sessionChanged = _conversationTarget is not null && _conversationTarget != target;
        _model.Text = target is null ? "No ready model · start a model on Home" : "Running model: " + target.Model;
        if (_main.IsOptimizing) _model.Text = "Chat is unavailable during performance optimization.";
        if (_cancellation is not null && sessionChanged)
            Cancel("Server session changed. Start a new chat after the model is ready.");
        _send.IsEnabled = !_clearing && _cancellation is null && target is not null && !sessionChanged && !string.IsNullOrWhiteSpace(_input.Text);
        _stop.IsEnabled = _cancellation is not null && !_cancellation.IsCancellationRequested;
        _clear.IsEnabled = !_clearing;
        _input.IsReadOnly = _cancellation is not null;
        _system.IsEnabled = _limit.IsEnabled = _cancellation is null;
        if (_cancellation is null && sessionChanged) _status.Text = "Server session changed. Select New chat to continue; the previous conversation remains visible.";
    }

    private void BeginSend()
    {
        UpdateState();
        if (!_send.IsEnabled) return;
        _operation = SendAsync();
    }

    private async Task SendAsync()
    {
        var target = _main.GetChatTarget();
        if (target is null) return;
        if (!int.TryParse(_limit.Text, out var maxTokens) || maxTokens is < 1 or > 131072)
        { _status.Text = "Maximum output tokens must be between 1 and 131072."; return; }
        var prompt = _input.Text?.Trim() ?? "";
        if (prompt.Length == 0) return;
        var request = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(_system.Text)) request.Add(new("system", _system.Text.Trim()));
        request.AddRange(_history); request.Add(new("user", prompt));
        _conversationTarget = target; _cancelReason = null; _cancellation = new CancellationTokenSource();
        _messages.Children.Remove(_empty);
        _messages.Children.Add(Message("You", Selectable(prompt), () => prompt));
        var reply = new Reply(); _reply = reply;
        reply.ThinkingPanel.Content = reply.Thinking;
        _messages.Children.Add(Message(target.Model, Stack(reply.ThinkingPanel, reply.Answer, reply.Status), () => reply.Content.ToString()));
        _input.Text = ""; _status.Text = "Waiting for the model…"; UpdateState();
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd());
        try
        {
            await foreach (var update in _client.StreamAsync(target, request, maxTokens, _cancellation.Token))
            {
                if (_main.GetChatTarget() != target) { Cancel("Server session changed. Start a new chat to continue."); _cancellation.Token.ThrowIfCancellationRequested(); }
                reply.Content.Append(update.Content); reply.Reasoning.Append(update.Reasoning); reply.Dirty = true;
                reply.FinishReason = update.FinishReason ?? reply.FinishReason;
                reply.PromptTokens = update.PromptTokens ?? reply.PromptTokens; reply.CompletionTokens = update.CompletionTokens ?? reply.CompletionTokens;
            }
            _cancellation.Token.ThrowIfCancellationRequested();
            if (reply.Content.Length == 0)
                throw new InvalidDataException("The model returned no answer text. If thinking used the output limit, increase it or lower the profile's reasoning budget.");
            _history.Add(new("user", prompt)); _history.Add(new("assistant", reply.Content.ToString()));
            reply.Status.Text = reply.FinishReason == "length" ? "Output limit reached · partial answer included in context" : "Complete";
            reply.Status.Text += $" · {reply.Timer.Elapsed.TotalSeconds:N1} s";
            if (reply.PromptTokens is { } input && reply.CompletionTokens is { } output) reply.Status.Text += $" · {input:N0} input / {output:N0} output tokens";
            _status.Text = reply.FinishReason == "length" ? "Output limit reached. Increase Maximum output tokens for longer responses." : "Ready for your next message.";
        }
        catch (OperationCanceledException)
        {
            reply.Status.Text = _cancelReason ?? "Request timed out after 10 minutes. Partial response is excluded from later requests.";
            _status.Text = reply.Status.Text; _input.Text = prompt;
        }
        catch (Exception ex)
        {
            reply.Status.Text = "Failed · this exchange is excluded from later requests";
            _status.Text = ex.Message; _input.Text = prompt;
        }
        finally
        {
            reply.Timer.Stop(); reply.Dirty = true; RenderReply();
            if (reply.Content.Length == 0) reply.Answer.Text = "No answer text received.";
            _reply = null; _cancellation.Dispose(); _cancellation = null; UpdateState();
        }
    }

    private void RenderReply()
    {
        if (_reply is not { } reply) return;
        var follow = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y < 100;
        if (reply.Dirty)
        {
            reply.Answer.Text = reply.Content.Length == 0 ? "Thinking…" : reply.Content.ToString();
            reply.Thinking.Text = reply.Reasoning.ToString(); reply.ThinkingPanel.IsVisible = reply.Reasoning.Length > 0;
            reply.ThinkingPanel.Header = $"Thinking · {reply.Reasoning.Length:N0} characters"; reply.Dirty = false;
            if (follow) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd());
        }
        if (reply.Timer.IsRunning && _cancellation?.IsCancellationRequested != true)
            _status.Text = $"{(reply.Content.Length > 0 ? "Generating" : reply.Reasoning.Length > 0 ? "Thinking" : "Waiting for the model")} · {reply.Timer.Elapsed.TotalSeconds:N0} s · {reply.Content.Length:N0} answer / {reply.Reasoning.Length:N0} thinking characters";
    }

    private void Cancel(string reason)
    {
        if (_cancellation is null) return;
        _cancelReason ??= reason; _cancellation.Cancel(); _stop.IsEnabled = false; _status.Text = "Stopping response…";
    }
    public async Task CancelAndWaitAsync()
    {
        Cancel("Stopped. Partial response is excluded from later requests.");
        await _operation;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); await CancelAndWaitAsync(); _client.Dispose();
    }
    private Border Message(string title, Control body, Func<string> copyText)
    {
        var copy = new Button { Content = "Copy", Padding = new Thickness(10, 4) };
        copy.Click += async (_, _) =>
        {
            try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(copyText()); }
            catch (Exception ex) { _status.Text = "Could not copy: " + ex.Message; }
        };
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(copy, 1); header.Children.Add(copy);
        return new Border { Classes = { "card" }, Child = Stack(header, body) };
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private static SelectableTextBlock Selectable(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static StackPanel Stack(params Control[] controls) { var panel = new StackPanel { Spacing = 8 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
    private static StackPanel Row(params Control[] controls) { var panel = Stack(controls); panel.Orientation = Orientation.Horizontal; return panel; }
}
