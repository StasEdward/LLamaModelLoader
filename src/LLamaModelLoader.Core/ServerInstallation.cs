namespace LLamaModelLoader.Core;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string Digest);

public sealed record ServerBuild(string Tag, DateTimeOffset PublishedAt, bool Prerelease, string Backend, ReleaseAsset[] Assets)
{
    public long DownloadBytes => Assets.Sum(a => a.Size);
    public string DirectoryName => $"{Tag}-{Backend}";
    public string DisplayName => $"{Tag} · {Backend.ToUpperInvariant()} · {PublishedAt:yyyy-MM-dd}" + (Prerelease ? " · rolling / prerelease" : "");
    public override string ToString() => DisplayName;
}

public sealed record InstalledServer(ServerBuild Build, DateTimeOffset InstalledAt, string Version, string ExecutablePath)
{
    public override string ToString() => Build.DisplayName;
}

public sealed record InstallationProgress(string Message, long DownloadedBytes = 0, long TotalBytes = 0);
