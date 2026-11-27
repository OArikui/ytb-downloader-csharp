namespace YtbDownloader.Queue;

public sealed record QueueStatus(
    DownloadRequest? Running,
    IReadOnlyList<DownloadRequest> Queued,
    bool Paused,
    int CompletedCount = 0);
