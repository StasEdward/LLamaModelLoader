using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public enum DownloadItemState { Queued, Downloading, Paused, Failed, Completed }

/// <summary>One queued download. Only the queue changes it, under its lock; observers get it through <see cref="DownloadQueue.Changed"/>.</summary>
public sealed class DownloadItem
{
    public required string RepoId { get; init; }
    public required string Sha { get; init; }
    public required HfQuantization Quantization { get; init; }
    public int ContextSize { get; init; }
    public string KvType { get; init; } = "";
    public FitResult? Fit { get; init; }
    public DownloadItemState State { get; internal set; } = DownloadItemState.Queued;
    public DownloadProgress? Progress { get; internal set; }
    public string Message { get; internal set; } = "";
    public string? FinalPath { get; internal set; }
    internal CancellationTokenSource? Cancellation { get; set; }
    internal TaskCompletionSource? Running { get; set; }
}

/// <summary>Runs queued downloads one at a time on a background worker. Events are raised on that worker thread, so UI observers marshal them themselves.</summary>
public sealed class DownloadQueue(Func<string> modelsDirectory, HttpClient http, Func<string, long>? freeSpace = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<DownloadItem> _items = [];
    private CancellationTokenSource _lifetime = new();
    private Task? _worker;

    public event Action<DownloadItem>? Changed;

    public IReadOnlyList<DownloadItem> Items { get { lock (_gate) return _items.ToArray(); } }

    public bool IsActive { get { lock (_gate) return _items.Any(i => i.State == DownloadItemState.Downloading); } }

    public DownloadItem Enqueue(string repoId, string sha, HfQuantization quant, int context, string kvType, FitResult? fit)
    {
        var folder = modelsDirectory();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new InvalidOperationException("Set a models folder in Settings first.");
        DownloadItem item;
        lock (_gate)
        {
            var same = _items.Where(i => i.RepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase) && i.Quantization.Label == quant.Label && i.Quantization.FirstPath == quant.FirstPath).ToArray();
            var existing = same.FirstOrDefault(i => i.State is DownloadItemState.Queued or DownloadItemState.Downloading);
            if (existing is not null) return existing;
            // A paused or failed duplicate is queued again, so its .part is picked up instead of a second row appearing.
            var retry = same.FirstOrDefault(i => i.State is DownloadItemState.Paused or DownloadItemState.Failed);
            if (retry is not null) Requeue(item = retry);
            else _items.Add(item = new() { RepoId = repoId, Sha = sha, Quantization = quant, ContextSize = context, KvType = kvType, Fit = fit });
            StartWorker();
        }
        Raise(item);
        return item;
    }

    public void Pause(DownloadItem item)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (item.State is not (DownloadItemState.Queued or DownloadItemState.Downloading)) return;
            item.State = DownloadItemState.Paused; item.Message = "Paused."; cancellation = item.Cancellation;
        }
        Cancel(cancellation);
        Raise(item);
    }

    public void Resume(DownloadItem item)
    {
        lock (_gate)
        {
            if (!_items.Contains(item) || item.State is not (DownloadItemState.Paused or DownloadItemState.Failed)) return;
            Requeue(item); StartWorker();
        }
        Raise(item);
    }

    /// <summary>Stops the item if it is running and deletes its .part files once the transfer has let go of them.</summary>
    public void Remove(DownloadItem item)
    {
        CancellationTokenSource? cancellation; Task? stopped;
        lock (_gate)
        {
            if (!_items.Remove(item)) return;
            cancellation = item.Cancellation; stopped = item.Running?.Task;
        }
        Cancel(cancellation);
        Raise(item);
        if (item.State == DownloadItemState.Completed) return;
        if (stopped is null || stopped.IsCompleted) DeletePartial(item);
        else _ = stopped.ContinueWith(_ => DeletePartial(item), TaskScheduler.Default);
    }

    public async Task CancelAllAsync()
    {
        CancellationTokenSource stopping; Task? worker; List<DownloadItem> changed;
        lock (_gate)
        {
            stopping = _lifetime; _lifetime = new(); worker = _worker;
            changed = _items.Where(i => i.State is DownloadItemState.Queued or DownloadItemState.Downloading).ToList();
            foreach (var item in changed) { item.State = DownloadItemState.Paused; item.Message = "Paused."; }
        }
        Cancel(stopping);
        foreach (var item in changed) Raise(item);
        if (worker is not null) await worker;
        stopping.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await CancelAllAsync();
        lock (_gate) _lifetime.Dispose();
    }

    private static void Requeue(DownloadItem item) { item.State = DownloadItemState.Queued; item.Message = ""; }

    private void StartWorker() => _worker ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (true)
        {
            DownloadItem item; CancellationTokenSource cancellation; TaskCompletionSource running;
            lock (_gate)
            {
                var next = _items.FirstOrDefault(i => i.State == DownloadItemState.Queued);
                if (next is null) { _worker = null; return; }
                item = next; item.State = DownloadItemState.Downloading; item.Message = "";
                cancellation = item.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                running = item.Running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Raise(item);
            try { await DownloadAsync(item, cancellation.Token); }
            finally
            {
                lock (_gate) { item.Cancellation = null; cancellation.Dispose(); }
                running.SetResult();
            }
        }
    }

    // A cancelled item was already marked Paused (or removed) by whoever cancelled it, so only completion and real failures are recorded here.
    private async Task DownloadAsync(DownloadItem item, CancellationToken token)
    {
        try
        {
            var downloader = new ModelDownloader(modelsDirectory(), http, freeSpace);
            var path = await downloader.DownloadAsync(item.RepoId, item.Sha, item.Quantization, new Sink(p => Update(item, i => i.Progress = p)), token);
            Update(item, i => { i.State = DownloadItemState.Completed; i.FinalPath = path; i.Message = "Downloaded."; }, finishing: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Update(item, i => { i.State = DownloadItemState.Failed; i.Message = ex.Message; });
        }
    }

    // Progress and failures only apply while the item is still the one being downloaded; a finished transfer counts even if a pause raced it.
    private void Update(DownloadItem item, Action<DownloadItem> change, bool finishing = false)
    {
        lock (_gate)
        {
            if (!_items.Contains(item) || (!finishing && item.State != DownloadItemState.Downloading)) return;
            change(item);
        }
        Raise(item);
    }

    private void DeletePartial(DownloadItem item)
    {
        try { new ModelDownloader(modelsDirectory(), http, freeSpace).DeletePartial(item.RepoId, item.Quantization); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { } // The folder changed or became unusable; nothing safe left to delete.
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // The transfer ended and released its token first.
    }

    private void Raise(DownloadItem item) => Changed?.Invoke(item);

    private sealed class Sink(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
