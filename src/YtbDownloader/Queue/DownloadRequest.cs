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

    public static string Describe(DownloadRequest req)
    {
        string kindLabel = req.Kind switch
        {
            "list" => "プレイリスト",
            "single" => "単体",
            _ => "",
        };
        string title = !string.IsNullOrEmpty(req.Title) ? req.Title
            : !string.IsNullOrEmpty(req.Description) ? req.Description
            : req.Url;
        string video = !string.IsNullOrEmpty(req.VideoId) ? $" [{req.VideoId}]" : "";
        return string.IsNullOrEmpty(kindLabel)
            ? $"{req.Mode} | {title}{video}"
            : $"{req.Mode} | {kindLabel} | {title}{video}";
    }
}
