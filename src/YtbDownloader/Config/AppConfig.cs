using System.IO;

namespace YtbDownloader.Config;

public sealed record AppConfig
{
    public required string DownloadDir { get; init; }
    public required string ArchiveFile { get; init; }
    public required string LogDir { get; init; }
    public string DefaultMode { get; init; } = "video"; // video|audio
    public string AudioFormat { get; init; } = "mp3";   // mp3|original
    public required string FfmpegLocation { get; init; }
    public string YtDlpLocation { get; init; } = "yt-dlp";
    public bool ForceIpv4 { get; init; } = true;
    public bool Subtitles { get; init; }
    public bool Thumbnails { get; init; }
    public bool EmbedMetadata { get; init; }
    public string? Cookies { get; init; }
    public bool EmbedAlbumArt { get; init; } = true;
    public int AudioBitrate { get; init; } = 256;
    public int? AudioBitDepth { get; init; }
    public int? AudioSampleRate { get; init; }
    public bool GenreLookup { get; init; } = true;
    public bool RecordAudioHash { get; init; } = true;

    public static AppConfig CreateDefault(string appDir) => new()
    {
        DownloadDir = Path.GetFullPath(Path.Combine(appDir, "downloads")),
        ArchiveFile = Path.GetFullPath(Path.Combine(appDir, "archive.txt")),
        LogDir = Path.GetFullPath(Path.Combine(appDir, "logs")),
        FfmpegLocation = Path.GetFullPath(Path.Combine(appDir, "ffmpeg")),
        YtDlpLocation = "yt-dlp",
    };
}
