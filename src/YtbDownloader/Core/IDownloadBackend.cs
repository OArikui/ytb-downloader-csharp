using YtbDownloader.Config;
using YtbDownloader.Queue;

namespace YtbDownloader.Core;

public sealed record DownloadInfo(string? Kind, string? Title, string? VideoId);

public interface IDownloadBackend
{
    Task DownloadAsync(
        DownloadRequest request,
        AppConfig config,
        Action<DownloadInfo>? infoCallback,
        CancellationToken ct = default);
}
