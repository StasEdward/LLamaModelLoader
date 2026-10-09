using System.Threading.Channels;

namespace LLamaModelLoader.Infrastructure;

public sealed class SessionLog : IAsyncDisposable
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Queue<string> _lines = new();
    private readonly Task _writer;
    private readonly Lock _sync = new();
    public string DirectoryPath { get; }
    public string? WriteError { get; private set; }
    public SessionLog(string directory) { DirectoryPath = directory; _writer = WriteAsync(); }
    public void Add(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {line[..Math.Min(line.Length, 4000)]}";
        lock (_sync) { _lines.Enqueue(entry); while (_lines.Count > 500) _lines.Dequeue(); }
        _channel.Writer.TryWrite(entry);
    }
    public string Snapshot() { lock (_sync) return string.Join(Environment.NewLine, _lines); }
    public void Clear() { lock (_sync) _lines.Clear(); }
    private async Task WriteAsync()
    {
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "server.log");
                if (File.Exists(path) && new FileInfo(path).Length > 4_000_000) File.Move(path, path + ".1", true);
                await File.AppendAllTextAsync(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteError = ex.Message; }
        }
    }
    public async ValueTask DisposeAsync() { _channel.Writer.TryComplete(); await _writer; }
}
