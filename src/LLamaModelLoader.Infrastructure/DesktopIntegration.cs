using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LLamaModelLoader.Infrastructure;

public static class WindowsStartup
{
    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the application path.");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use the published application executable to enable startup with Windows.");
            key.SetValue("LLamaModelLoader", $"\"{executable}\"");
        }
        else key.DeleteValue("LLamaModelLoader", false);
    }
}

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    public bool IsFirst { get; }
    public SingleInstance(string scope)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + scope)))[..24];
        _pipe = "LLamaModelLoader-" + id;
        _mutex = new Mutex(true, @"Local\" + _pipe, out var created);
        IsFirst = created;
    }
    public async Task NotifyAsync()
    {
        using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(3000); await pipe.WriteAsync(new byte[] { 1 }); }
        catch (TimeoutException) { }
    }
    public async Task ListenAsync(Action activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var data = new byte[1];
                if (await pipe.ReadAsync(data, _stop.Token) > 0) activate();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
        }
    }
    public void Dispose() { _stop.Cancel(); if (IsFirst) _mutex.ReleaseMutex(); _mutex.Dispose(); _stop.Dispose(); }
}
