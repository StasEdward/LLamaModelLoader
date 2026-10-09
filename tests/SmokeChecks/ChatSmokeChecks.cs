using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Core;
using LLamaModelLoader.Desktop;

public static class ChatSmokeChecks
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, string directory)
    {
        void Navigate(string label) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Navigate("◌  Chat"); await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        ChatView View() => window.GetVisualDescendants().OfType<ChatView>().Single();
        Button Button(string name) => View().GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
        TextBox Input() => View().GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ChatInput");
        string? Status() => View().GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChatStatus").Text;
        string? Model() => View().GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChatModel").Text;
        string[] Answers() => View().GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text ?? "").ToArray();
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        async Task WaitAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { while (!condition()) await Task.Delay(30, timeout.Token); }
            catch (OperationCanceledException) { throw new Exception("Chat check timed out: " + Status()); }
        }
        async Task SnapshotAsync(string name)
        {
            await Task.Delay(150); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No chat frame");
            bitmap.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Input().Text = "Hello";
        if (Button("ChatSend").IsEnabled) throw new Exception("Chat can send without a ready server");
        await SnapshotAsync("chat-stopped.png");
        await vm.StartCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.Status.State == ServerState.Ready && Button("ChatSend").IsEnabled);
        var runningName = vm.SelectedProfile!.Name;
        Click("ChatSend");
        await WaitAsync(() => Status() == "Ready for your next message.");
        if (!Answers().Any(a => a.Contains("Received 1 message(s)."))) throw new Exception("First chat answer not streamed");
        var thinking = View().GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString()?.StartsWith("Thinking") == true);
        thinking.IsExpanded = true; await Task.Delay(80);
        if (!Answers().Contains("Checking the test message.")) throw new Exception("Reasoning not displayed separately");
        await SnapshotAsync("chat-response.png");
        thinking.IsExpanded = false;
        var edited = OptimizationPlan.Copy(vm.SelectedProfile!); edited.Name = "Renamed after server start";
        await vm.SaveProfileAsync(edited); await Task.Delay(150);
        if (Model() != "Running model: " + runningName) throw new Exception("Chat used an unsaved-to-server alias");
        Navigate("◉  Home"); Navigate("◌  Chat"); await Task.Delay(100);
        Input().Text = "A follow-up message"; Click("ChatSend");
        await WaitAsync(() => Status() == "Ready for your next message.");
        if (!Answers().Any(a => a.Contains("Received 3 message(s)."))) throw new Exception("Conversation history lost on navigation");
        Input().Text = "[stall]"; Click("ChatSend");
        await WaitAsync(() => Answers().Count(a => a.StartsWith("Hello from")) == 3);
        Click("ChatStop"); await WaitAsync(() => !Button("ChatStop").IsEnabled && Status()?.StartsWith("Stopped by you") == true);
        if (Input().Text != "[stall]" || vm.Status.State != ServerState.Ready) throw new Exception("Stop response changed server state or lost prompt");
        Input().Text = "After cancellation"; Click("ChatSend");
        await WaitAsync(() => Status() == "Ready for your next message.");
        if (!Answers().Any(a => a.Contains("Received 5 message(s)."))) throw new Exception("Canceled exchange leaked into history");
        Input().Text = "[stall]"; Click("ChatSend"); await Task.Delay(150);
        await vm.StopCommand.ExecuteAsync(null); await window.CancelChatAsync();
        await vm.StartCommand.ExecuteAsync(null); await WaitAsync(() => vm.Status.State == ServerState.Ready);
        await Task.Delay(200); Input().Text = "New session";
        if (Button("ChatSend").IsEnabled || Status()?.Contains("New chat") != true) throw new Exception("Old conversation crossed server sessions");
        Click("ChatClear"); await WaitAsync(() => Input().Text == "");
        Input().Text = "Fresh conversation"; Click("ChatSend");
        await WaitAsync(() => Status() == "Ready for your next message.");
        if (!Answers().Any(a => a.Contains("Received 1 message(s)."))) throw new Exception("New chat did not reset context");
        if (Model() != "Running model: " + edited.Name) throw new Exception("Chat did not use restarted alias");
        Input().Text = "[stall]"; Click("ChatSend"); await Task.Delay(150);
        Click("ChatClear"); await WaitAsync(() => Input().Text == "" && Button("ChatClear").IsEnabled);
        if (Answers().Length != 0) throw new Exception("New chat during generation retained a partial response");
        await vm.StopCommand.ExecuteAsync(null);
        Console.WriteLine("UI: chat streaming, reasoning, multi-turn history, navigation, live alias, stop, session changes, and clear passed");
    }
}
