using System.IO;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace YtbDownloader.Config;

public static class ConfigLoader
{
    public static AppConfig Load(string configPath, string appDir)
    {
        appDir = Path.GetFullPath(appDir);
        configPath = ResolveConfigPath(configPath);

        TomlTable table = new();
        if (File.Exists(configPath))
        {
            try
            {
                string text = File.ReadAllText(configPath, Encoding.UTF8);
                table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
            }
            catch (TomlException ex)
            {
                string message = ex.Message;
                if (message.Contains("Unescaped", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("backslash", StringComparison.OrdinalIgnoreCase))
                {
                    message +=
                        "\nWindowsのパスは、ffmpeg_location = 'E:\\ffmpeg' のように" +
                        "シングルクォートで囲んでください。";
                }

                throw new InvalidOperationException(
                    $"設定ファイルのTOML形式が不正です:{configPath}\n{message}", ex);
            }
        }
        else
        {
            throw new InvalidOperationException($"設定ファイルが見つかりません: {configPath}");
        }

        string baseDir = Path.GetDirectoryName(configPath) ?? appDir;
        string defaultMode = GetString(table, "default_mode", "video");
        if (defaultMode is not ("video" or "audio"))
            throw new InvalidOperationException("default_mode は video または audio にしてください。");

        string audioFormat = GetString(table, "audio_format", "mp3");
        if (audioFormat is not ("mp3" or "original"))
            throw new InvalidOperationException("audio_format は mp3 または original にしてください。");

        int audioBitrate = GetInt(table, "audio_bitrate", 256);
        if (audioBitrate <= 0)
            throw new InvalidOperationException("audio_bitrate は正の整数で指定してください。");

        int? audioBitDepth = GetOptionalInt(table, "audio_bit_depth");
        if (audioBitDepth is not null && audioBitDepth is not (16 or 24 or 32))
            throw new InvalidOperationException("audio_bit_depth は 16、24、32 のいずれかで指定してください。");

        int? audioSampleRate = GetOptionalInt(table, "audio_sample_rate");
        if (audioSampleRate is not null && audioSampleRate <= 0)
            throw new InvalidOperationException("audio_sample_rate は正の整数で指定してください。");

        string cookiesValue = GetString(table, "cookies", "");
        string? cookies = string.IsNullOrEmpty(cookiesValue)
            ? null
            : ResolvePath(cookiesValue, baseDir);

        string ytDlpLocation = GetString(table, "yt_dlp_location", "yt-dlp");
        if (ytDlpLocation.Contains('\\') || ytDlpLocation.Contains('/') || Path.IsPathRooted(ytDlpLocation))
            ytDlpLocation = ResolvePath(ytDlpLocation, appDir);

        return new AppConfig
        {
            DownloadDir = ResolvePath(GetString(table, "download_dir", "downloads"), baseDir),
            ArchiveFile = ResolvePath(GetString(table, "archive_file", "archive.txt"), baseDir),
            LogDir = ResolvePath(GetString(table, "log_dir", "logs"), baseDir),
            DefaultMode = defaultMode,
            AudioFormat = audioFormat,
            FfmpegLocation = ResolvePath(GetString(table, "ffmpeg_location", "ffmpeg"), appDir),
            YtDlpLocation = ytDlpLocation,
            ForceIpv4 = GetBool(table, "force_ipv4", true),
            Subtitles = GetBool(table, "subtitles", false),
            Thumbnails = GetBool(table, "thumbnails", false),
            EmbedMetadata = GetBool(table, "embed_metadata", false),
            Cookies = cookies,
            EmbedAlbumArt = GetBool(table, "embed_album_art", true),
            AudioBitrate = audioBitrate,
            AudioBitDepth = audioBitDepth,
            AudioSampleRate = audioSampleRate,
            GenreLookup = GetBool(table, "genre_lookup", true),
            RecordAudioHash = GetBool(table, "record_audio_hash", true),
        };
    }

    private static string ResolveConfigPath(string path)
    {
        string expanded = ExpandHome(path);
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(Environment.CurrentDirectory, expanded);
        return Path.GetFullPath(expanded);
    }

    private static string ResolvePath(string value, string baseDir)
    {
        string expanded = ExpandHome(value);
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(baseDir, expanded);
        return Path.GetFullPath(expanded);
    }

    private static string ExpandHome(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, value[2..]);
        }

        return value;
    }

    private static object? GetValue(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) ? value : null;

    private static string GetString(TomlTable table, string key, string defaultValue)
    {
        object? value = GetValue(table, key);
        if (value is null)
            return defaultValue;
        if (value is string text)
            return text;
        throw new InvalidOperationException($"{key} は文字列で指定してください。");
    }

    private static bool GetBool(TomlTable table, string key, bool defaultValue)
    {
        object? value = GetValue(table, key);
        if (value is null)
            return defaultValue;
        if (value is bool flag)
            return flag;
        throw new InvalidOperationException($"{key}は true または false で指定してください。");
    }

    private static int GetInt(TomlTable table, string key, int defaultValue)
    {
        object? value = GetValue(table, key);
        if (value is null)
            return defaultValue;
        return ConvertToInt(value, key);
    }

    private static int? GetOptionalInt(TomlTable table, string key)
    {
        object? value = GetValue(table, key);
        if (value is null)
            return null;
        return ConvertToInt(value, key);
    }

    private static int ConvertToInt(object value, string key)
    {
        if (value is bool)
            throw new InvalidOperationException($"{key} は整数で指定してください。");

        if (value is long l)
            return checked((int)l);
        if (value is int i)
            return i;
        if (value is double d && Math.Abs(d - Math.Truncate(d)) < double.Epsilon)
            return checked((int)d);

        throw new InvalidOperationException($"{key} は整数で指定してください。");
    }
}
