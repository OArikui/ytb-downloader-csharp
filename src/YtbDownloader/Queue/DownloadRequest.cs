using YtbDownloader.Core;

namespace YtbDownloader.Queue;

public sealed class DownloadRequest
{
    public required string Url { get; set; }
    public required string Mode { get; set; } // audio|video
    public string Description { get; set; } = "";
    public string? Kind { get; set; } // "list" | "single" | null
    public string? VideoId { get; set; }
    public string? Title { get; set; }
    public int Priority { get; init; }
    public bool Done { get; set; }
    public bool Skipped { get; set; }
    public string? Error { get; set; }

    /// <summary>トレイ入力で指定された yt-dlp オプション。</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>情報取得時に確定した Python 版準拠の曲名(audio モード用)。</summary>
    public string? ResolvedTitle { get; set; }

    /// <summary>キューの安定した FIFO 順序用の連番。</summary>
    public long Sequence { get; set; }

    /// <summary>タイトルの先行取得が完了したか。</summary>
    public bool TitleProbed { get; set; }

    /// <summary>実行中の進捗(percent/speed/ETA)。</summary>
    public DownloadProgress? Progress { get; set; }

    public static string Describe(DownloadRequest req)
    {
        string kindLabel = req.Kind switch
        {
            "list" => "プレイリスト",
            "single" => "単体",
            _ => "",
        };
        string title = !string.IsNullOrEmpty(req.ResolvedTitle) ? req.ResolvedTitle
            : !string.IsNullOrEmpty(req.Title) ? req.Title
            : !string.IsNullOrEmpty(req.Description) ? req.Description
            : req.Url;
        string video = !string.IsNullOrEmpty(req.VideoId) ? $" [{req.VideoId}]" : "";
        return string.IsNullOrEmpty(kindLabel)
            ? $"{req.Mode} | {title}{video}"
            : $"{req.Mode} | {kindLabel} | {title}{video}";
    }
}
