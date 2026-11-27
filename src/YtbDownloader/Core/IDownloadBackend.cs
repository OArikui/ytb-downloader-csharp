using YtbDownloader.Config;
using YtbDownloader.Queue;

namespace YtbDownloader.Core;

/// <summary>
/// 情報取得(プローブ)またはダウンロード時に得られた動画/音声の情報。
/// ResolvedTitle は audio モードで Python 版の命名規則から確定した曲名。
/// </summary>
public sealed record DownloadInfo(
    string? Kind,
    string? Title,
    string? VideoId,
    string? Uploader = null,
    string? Track = null,
    string? Artist = null,
    string? ResolvedTitle = null);

/// <summary>yt-dlp の進捗行から取り出した実行中の進捗。</summary>
public sealed record DownloadProgress(
    double? Percent,
    string? Speed,
    string? Eta);

public interface IDownloadBackend
{
    /// <summary>
    /// ダウンロードせずにタイトル種別等信息だけを取得する(キューの先行取得用)。
    /// </summary>
    Task<DownloadInfo> ProbeAsync(
        DownloadRequest request,
        AppConfig config,
        CancellationToken ct = default);

    Task DownloadAsync(
        DownloadRequest request,
        AppConfig config,
        Action<DownloadInfo>? infoCallback,
        Action<DownloadProgress>? progressCallback = null,
        CancellationToken ct = default);
}
