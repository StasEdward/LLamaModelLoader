using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace LLamaModelLoader.Desktop;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private bool _exiting;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _viewModel = new MainViewModel(Program.DataDirectory);
            var window = new MainWindow(_viewModel);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LLamaModelLoader.Desktop/Assets/icon.png")));
            window.Icon = icon;
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open"); open.Click += (_, _) => Show(window);
            menu.Items.Add(open);
            var statistics = new NativeMenuItem("Statistics"); statistics.Click += (_, _) => window.ShowStatistics(); menu.Items.Add(statistics);
            menu.Items.Add(new NativeMenuItem("Start") { Command = _viewModel.StartCommand });
            menu.Items.Add(new NativeMenuItem("Stop") { Command = _viewModel.StopCommand });
            menu.Items.Add(new NativeMenuItem("Restart") { Command = _viewModel.RestartCommand });
            menu.Items.Add(new NativeMenuItemSeparator());
            var exit = new NativeMenuItem("Exit and stop server"); exit.Click += async (_, _) => await ExitAsync(); menu.Items.Add(exit);
            _tray = new TrayIcon { Icon = icon, ToolTipText = "Llama Model Loader", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => Show(window);
            TrayIcon.SetIcons(this, [_tray]);
            window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                if (_tray.NativeMenuExporter is not null) window.Hide(); else _ = ExitAsync();
            };
            desktop.ShutdownRequested += async (_, e) => { if (!_exiting) { e.Cancel = true; await ExitAsync(); } };
            window.Opened += async (_, _) =>
            {
                _ = Program.Instance.ListenAsync(() => Dispatcher.UIThread.Post(() => Show(window)));
                if (_viewModel.HadConfiguration && !_viewModel.IsReadOnly && string.IsNullOrEmpty(_viewModel.Notice) &&
                    File.Exists(_viewModel.Configuration.Settings.ServerPath) && _viewModel.SelectedProfile is not null)
                {
                    if (_viewModel.Configuration.Settings.StartMinimized && _tray.NativeMenuExporter is not null) window.Hide();
                    if (_viewModel.Configuration.Settings.AutoStartModel) await _viewModel.StartCommand.ExecuteAsync(null);
                    if (_viewModel.Notice.Length > 0) Show(window);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static void Show(Window window) { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow main) await main.CancelInstallationAsync();
            if (_viewModel is not null) await _viewModel.DisposeAsync();
        }
        finally { _tray?.Dispose(); (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(); }
    }
}
