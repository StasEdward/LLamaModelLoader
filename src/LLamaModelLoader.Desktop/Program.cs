using Avalonia;
using System.Globalization;
using LLamaModelLoader.Infrastructure;

namespace LLamaModelLoader.Desktop;

internal static class Program
{
    public static SingleInstance Instance { get; private set; } = null!;
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("LLAMAMODELLOADER_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLamaModelLoader");
    [STAThread]
    public static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        using var instance = new SingleInstance(DataDirectory);
        Instance = instance;
        if (!instance.IsFirst) { instance.NotifyAsync().GetAwaiter().GetResult(); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
