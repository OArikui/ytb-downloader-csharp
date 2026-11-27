using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YtbDownloader.Config;
using YtbDownloader.Logging;
using YtbDownloader.Queue;
using YtbDownloader.Tray;

namespace YtbDownloader.Core;

public sealed class YtDlpBackend : IDownloadBackend
{
    // Older ffmpeg prints "SHA256=<hash>", newer versions print a bare hash
    // in the final CSV line. Accept both formats.
    private static readonly Regex StreamHashPattern = new(
        @"(?:SHA256=)?([0-9a-fA-F]{64})", RegexOptions.Compiled);

    // yt-dlp の進捗行 (例: [download]  45.2% of 10.00MiB at  1.20MiB/s ETA 00:12)
    private static readonly Regex ProgressPattern = new(
        @"\[download\]\s+(?<pct>\d+(?:\.\d+)?)%(?:\s+of\s+(?:~\s+)?[\d.]+\s*[KMGTPE]?i?B)?(?:\s+at\s+(?<speed>\S+))?(?:\s+ETA\s+(?<eta>\S+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // パイプラインが使用するオプションは上書きを許可しない。
    private static readonly HashSet<string> ReservedOptions = new(
        new[]
        {
            "o", "output", "print", "download-archive", "ignore-config",
            "newline", "progress", "no-progress", "continue",
            "dump-single-json", "skip-download", "flat-playlist",
            "ffmpeg-location", "source-address",
        },
        StringComparer.OrdinalIgnoreCase);

    // 情報取得(JSON)の段階では、一覧表示系のオプションは JSON を壊すため除外する。
    private static readonly HashSet<string> NonJsonActions = new(
        new[]
        {
            "F", "list-formats", "list-subs", "list-thumbnails", "simulate",
            "get-url", "get-title", "get-id", "get-description",
            "get-duration", "get-thumbnail", "get-webpage-url", "print-to-file",
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly MusicBrainzGenreLookup _genreLookup = new();

    /// <summary> tray 実行中の問題点記録用。</summary>
    public TrayIssueRecorder? IssueRecorder { get; set; }

    /// <summary>
    /// URL から動画 ID を取り出し、アーカイブ済みかどうかを確認する。
    /// リンクリストの事前スキップ判定に使用する（Python 版の
    /// _video_id_from_url + _archive_contains 相当）。
    /// </summary>
    public static bool IsArchivedUrl(string url, AppConfig config) =>
        ArchiveContains(config, VideoIdFromUrl(url));

    /// <summary>
    /// ダウンロードせずにタイトル/種別を取得する。キューの全タスクに対して
    /// ダウンロードワーカーとは別に実行され、表示と割り込み判定に使われる。
    /// audio モードでは Python 版 build_audio_name 相当の曲名を確定する。
    /// </summary>
    public async Task<DownloadInfo> ProbeAsync(
        DownloadRequest request,
        AppConfig config,
        CancellationToken ct = default)
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--dump-single-json",
            "--skip-download",
            "--no-progress",
            "--flat-playlist",
        };
        if (config.ForceIpv4)
        {
            arguments.Add("--source-address");
            arguments.Add("0.0.0.0");
        }
        arguments.AddRange(FilterInfoArgs(request.ExtraArgs));
        arguments.Add(request.Url);

        try
        {
            ProcessResult result = await RunProcessAsync(
                    ResolveYtDlp(config), arguments, progressCallback: null, ct: ct)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                return new DownloadInfo(null, null, null);

            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            JsonElement root = document.RootElement;
            string type = GetString(root, "_type");
            bool isPlaylist = type is "playlist" or "multi_video"
                || root.TryGetProperty("entries", out _);
            string title = GetString(root, "title");
            string uploader = GetString(root, "uploader");
            string id = GetString(root, "id");

            if (isPlaylist)
                return new DownloadInfo("list", title, id, uploader);

            string? track = GetStringOrNull(root, "track");
            string? artist = GetStringOrNull(root, "artist")
                ?? GetStringOrNull(root, "artists");
            string? resolved = request.Mode == "audio"
                ? NamingService.BuildAudioName(title, uploader, track, artist).Filename
                : null;
            return new DownloadInfo(
                "single",
                resolved ?? title,
                id,
                uploader,
                track,
                artist,
                resolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn($"タイトル情報の取得に失敗しました: {request.Url}: {ex.Message}");
            return new DownloadInfo(null, null, null);
        }
    }

    public async Task DownloadAsync(
        DownloadRequest request,
        AppConfig config,
        Action<DownloadInfo>? infoCallback,
        Action<DownloadProgress>? progressCallback = null,
        CancellationToken ct = default)
    {
        if (request.Mode is not ("video" or "audio"))
            throw new ArgumentException($"不正なモードです:{request.Mode}", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Url))
            throw new ArgumentException("URLが指定されていません。", nameof(request));

        Directory.CreateDirectory(config.DownloadDir);
        Directory.CreateDirectory(Path.GetDirectoryName(config.ArchiveFile) ?? config.DownloadDir);
        Directory.CreateDirectory(config.LogDir);

        AppLogger.Info("ダウンロードを開始します。");
        AppLogger.Info($"URL: {request.Url}");
        AppLogger.Info($"モード: {request.Mode}");
        AppLogger.Info($"IPv4固定: {(config.ForceIpv4 ? "有効" : "無効")}");
        IReadOnlyList<string> extraArgs = FilterExtraArgs(request.ExtraArgs);

        (string? playlistItem, string downloadUrl) = ResolvePlaylistItemSelection(request.Url);
        ExtractionResult extraction = await ExtractAsync(
                downloadUrl, playlistItem, config, extraArgs, ct)
            .ConfigureAwait(false);

        int? playlistTotal = extraction.PlaylistCount
            ?? (playlistItem is null && extraction.Entries.Count > 0
                ? extraction.Entries.Count
                : null);
        if (playlistItem is not null
            && extraction.IsPlaylist
            && playlistTotal is int total
            && int.TryParse(playlistItem, out int requestedIndex)
            && requestedIndex > total)
        {
            AppLogger.Warn(
                $"プレイリストの index が範囲外です: {playlistItem}（全{total}件）。" +
                "単体動画としてダウンロードします。");
            playlistItem = null;
            downloadUrl = SingleVideoUrl(request.Url);
            extraction = await ExtractAsync(downloadUrl, null, config, extraArgs, ct)
                .ConfigureAwait(false);
        }

        if (extraction.IsPlaylist && extraction.Entries.Count == 0)
        {
            AppLogger.Info("すべて取得済みです。");
            request.Skipped = true;
            return;
        }

        if (extraction.IsPlaylist
            && playlistItem is null
            && extraction.Entries.Count > 0
            && extraction.Entries.All(entry => ArchiveContains(config, entry.Id)))
        {
            AppLogger.Info("すべて取得済みです。");
            request.Skipped = true;
            return;
        }

        MediaEntry? selectedEntry = null;
        if (playlistItem is not null && extraction.Entries.Count > 0)
        {
            int index = int.Parse(playlistItem);
            int entryIndex = extraction.Entries.Count == 1
                ? 0
                : Math.Min(index - 1, extraction.Entries.Count - 1);
            selectedEntry = extraction.Entries[entryIndex];
            if (ArchiveContains(config, selectedEntry.Id))
            {
                AppLogger.Info("すべて取得済みです。");
                request.Skipped = true;
                return;
            }
        }
        else if (!extraction.IsPlaylist)
        {
            selectedEntry = extraction.Entries.FirstOrDefault();
            if (selectedEntry is not null && ArchiveContains(config, selectedEntry.Id))
            {
                AppLogger.Info("すべて取得済みです。");
                request.Skipped = true;
                return;
            }
        }

        // audio モードでは Python 版と同じ曲名をダウンロード前に確定させる。
        string? resolvedAudioTitle = null;
        if (request.Mode == "audio" && selectedEntry is not null)
        {
            resolvedAudioTitle = NamingService.BuildAudioName(
                selectedEntry.Title,
                selectedEntry.Uploader,
                selectedEntry.Track,
                selectedEntry.Artist).Filename;
        }
        if (request.Mode == "audio")
            request.ResolvedTitle = resolvedAudioTitle;

        infoCallback?.Invoke(new DownloadInfo(
            extraction.IsPlaylist ? "list" : "single",
            resolvedAudioTitle ?? extraction.Title,
            VideoIdFromUrl(downloadUrl) ?? extraction.Id,
            selectedEntry?.Uploader,
            selectedEntry?.Track,
            selectedEntry?.Artist,
            resolvedAudioTitle));

        string outputDir = extraction.IsPlaylist
            ? Path.Combine(
                config.DownloadDir,
                NamingService.SanitizeFilename(
                    string.IsNullOrWhiteSpace(extraction.Title) ? "playlist" : extraction.Title))
            : config.DownloadDir;
        Directory.CreateDirectory(outputDir);

        List<string> arguments = BuildDownloadArguments(
            config,
            request.Mode,
            outputDir,
            playlistItem,
            resolvedAudioTitle,
            extraArgs);
        arguments.Add(downloadUrl);

        ProcessResult result = await RunProcessAsync(
                ResolveYtDlp(config), arguments, progressCallback, ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Log403Diagnostics(result.Stderr, config);
            throw new InvalidOperationException(
                $"yt-dlp の実行に失敗しました（終了コード {result.ExitCode}）: " +
                Tail(result.Stderr, 2000));
        }

        var downloaded = new List<(string File, MediaEntry Entry)>();
        Dictionary<string, MediaEntry> entriesById = extraction.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (result.Files.Count == 0)
        {
            if (extraction.Entries.Any(entry => ArchiveContains(config, entry.Id)))
            {
                AppLogger.Info("すべて取得済みです。");
                request.Skipped = true;
                return;
            }

            AppLogger.Warn("ダウンロードされたファイルパスを取得できませんでした。");
        }

        for (int i = 0; i < result.Files.Count; i++)
        {
            string file = result.Files[i].Path;
            if (!File.Exists(file))
            {
                AppLogger.Warn($"ダウンロードファイルが見つかりません: {file}");
                continue;
            }

            // audio モードではテンプレートに ID を含めないため、
            // --print 出力の ID を優先してエントリを対応付ける。
            string id = result.Files[i].Id;
            if (string.IsNullOrEmpty(id))
                id = VideoIdFromFileName(file);
            MediaEntry? entry = null;
            if (id.Length > 0)
                entriesById.TryGetValue(id, out entry);
            entry ??= selectedEntry
                ?? extraction.Entries.ElementAtOrDefault(i)
                ?? new MediaEntry(extraction.Id, extraction.Title, extraction.Uploader, null, null);

            string extension = Path.GetExtension(file);
            string desired = request.Mode == "video"
                ? NamingService.BuildVideoName(entry.Title, entry.Id.Length > 0 ? entry.Id : id)
                : NamingService.BuildAudioName(
                    entry.Title,
                    entry.Uploader,
                    entry.Track,
                    entry.Artist).Filename;

            string target = Path.Combine(
                Path.GetDirectoryName(file) ?? outputDir,
                desired + extension);
            if (!string.Equals(
                    Path.GetFullPath(file),
                    Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                target = EnsureUniquePath(target);
                File.Move(file, target);
                AppLogger.Info(
                    $"ファイル名を変更しました: {Path.GetFileName(file)} -> " +
                    $"{Path.GetFileName(target)}");
                file = target;
            }

            downloaded.Add((file, entry));
        }

        if (request.Mode == "audio")
        {
            foreach ((string file, MediaEntry entry) in downloaded)
            {
                try
                {
                    await PostProcessAudioAsync(
                        request, file, entry, config, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"音声の後処理に失敗しました: {file}: {ex.Message}");
                }
            }

            CleanupEmbedThumbnails(config, outputDir);
        }

        AppLogger.Info("ダウンロード処理が完了しました。");
    }

    private static List<string> BuildDownloadArguments(
        AppConfig config,
        string mode,
        string outputDir,
        string? playlistItem,
        string? audioTitle,
        IReadOnlyList<string> extraArgs)
    {
        // Python 版は audio モードで "曲名 - アーティスト.拡張子" を
        // テンプレートに直接渡す。C# 版も ID なしテンプレートに合わせ、
        // 必要な場合のみ後でリネームする。
        string extensionTemplate = mode == "audio"
            ? (string.IsNullOrWhiteSpace(audioTitle)
                ? "%(title)s.%(ext)s"
                : NamingService.SanitizeFilename(audioTitle) + ".%(ext)s")
            : "%(title)s [%(id)s].%(ext)s";

        var arguments = new List<string>
        {
            "--ignore-config",
            "--newline",
            "--progress",
            "--continue",
            "--retries", "3",
            "--fragment-retries", "3",
            "--download-archive", config.ArchiveFile,
            "-o", Path.Combine(outputDir, extensionTemplate),
            "--print", "after_move:FILE:%(id)s|%(filepath)s",
        };

        if (config.ForceIpv4)
        {
            arguments.Add("--source-address");
            arguments.Add("0.0.0.0");
        }

        string? ffmpeg = ResolveFfmpeg(config);
        if (ffmpeg is not null
            && (Path.IsPathRooted(ffmpeg) || ffmpeg.Contains('\\') || ffmpeg.Contains('/'))
            && (File.Exists(ffmpeg) || Directory.Exists(ffmpeg)))
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(ffmpeg);
        }

        if (config.Subtitles)
        {
            arguments.Add("--write-subs");
            arguments.Add("--all-subs");
        }

        bool writeThumbnail = config.Thumbnails
            || (mode == "audio"
                && config.AudioFormat == "mp3"
                && config.EmbedAlbumArt);
        if (writeThumbnail)
            arguments.Add("--write-thumbnail");

        if (mode == "video")
        {
            arguments.Add("-f");
            arguments.Add("bestvideo+bestaudio/best");
            arguments.Add("--merge-output-format");
            arguments.Add("mp4");
        }
        else
        {
            arguments.Add("-f");
            arguments.Add("bestaudio/best");

            if (config.AudioFormat == "mp3")
            {
                arguments.Add("-x");
                arguments.Add("--audio-format");
                arguments.Add("mp3");
                arguments.Add("--audio-quality");
                arguments.Add(config.AudioBitrate.ToString());

                if (config.EmbedAlbumArt)
                    arguments.Add("--embed-thumbnail");
                if (config.EmbedMetadata)
                    arguments.Add("--embed-metadata");

                var audioArgs = new List<string>();
                if (config.AudioSampleRate is int sampleRate)
                {
                    audioArgs.Add("-ar");
                    audioArgs.Add(sampleRate.ToString());
                }
                if (config.AudioBitDepth is int bitDepth)
                {
                    audioArgs.Add("-sample_fmt");
                    audioArgs.Add(bitDepth == 16 ? "s16p" : "s32p");
                }
                if (audioArgs.Count > 0)
                {
                    arguments.Add("--postprocessor-args");
                    arguments.Add("FFmpegExtractAudio:" + string.Join(" ", audioArgs));
                }
            }
            else if (config.EmbedMetadata)
            {
                arguments.Add("--embed-metadata");
            }
        }

        arguments.AddRange(extraArgs);

        if (playlistItem is not null)
        {
            arguments.Add("--playlist-items");
            arguments.Add(playlistItem);
        }

        return arguments;
    }

    private static async Task<ExtractionResult> ExtractAsync(
        string url,
        string? playlistItem,
        AppConfig config,
        IReadOnlyList<string> extraArgs,
        CancellationToken ct)
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--dump-single-json",
            "--skip-download",
            "--no-progress",
        };

        if (config.ForceIpv4)
        {
            arguments.Add("--source-address");
            arguments.Add("0.0.0.0");
        }

        arguments.AddRange(FilterInfoArgs(extraArgs));
        if (playlistItem is not null)
        {
            arguments.Add("--playlist-items");
            arguments.Add(playlistItem);
        }

        arguments.Add(url);

        ProcessResult result = await RunProcessAsync(
                ResolveYtDlp(config), arguments, progressCallback: null, ct: ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Log403Diagnostics(result.Stderr, config);
            throw new InvalidOperationException(
                "動画情報を取得できませんでした: " + Tail(result.Stderr, 2000));
        }
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException("動画情報を取得できませんでした。");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        string type = GetString(root, "_type");
        if (type == "channel")
        {
            throw new InvalidOperationException(
                "チャンネル全動画の一括ダウンロードには対応していません。");
        }

        bool isPlaylist = type is "playlist" or "multi_video";
        string title = GetString(root, "title");
        string uploader = GetString(root, "uploader");
        string id = GetString(root, "id");

        int? playlistCount = null;
        if (root.TryGetProperty("playlist_count", out JsonElement countElement)
            && countElement.ValueKind == JsonValueKind.Number
            && countElement.TryGetInt32(out int count))
        {
            playlistCount = count;
        }

        var entries = new List<MediaEntry>();
        if (root.TryGetProperty("entries", out JsonElement entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in entriesElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                    entries.Add(ToMediaEntry(entry, title, uploader));
            }
        }
        else if (!isPlaylist)
        {
            entries.Add(ToMediaEntry(root, title, uploader));
        }

        return new ExtractionResult(
            isPlaylist,
            title,
            uploader,
            id,
            entries,
            playlistCount);
    }

    private static MediaEntry ToMediaEntry(
        JsonElement element,
        string fallbackTitle,
        string fallbackUploader)
    {
        string id = GetString(element, "id");
        string title = GetString(element, "title");
        if (title.Length == 0)
            title = fallbackTitle;
        string uploader = GetString(element, "uploader");
        if (uploader.Length == 0)
            uploader = fallbackUploader;

        string? track = GetStringOrNull(element, "track");
        string? artist = GetStringOrNull(element, "artist")
            ?? GetStringOrNull(element, "artists");

        return new MediaEntry(id, title, uploader, track, artist);
    }

    private async Task PostProcessAudioAsync(
        DownloadRequest request,
        string file,
        MediaEntry entry,
        AppConfig config,
        CancellationToken ct)
    {
        AudioName naming = NamingService.BuildAudioName(
            entry.Title,
            entry.Uploader,
            entry.Track,
            entry.Artist);

        bool missingName = string.IsNullOrWhiteSpace(naming.Track)
            || string.IsNullOrWhiteSpace(naming.Artist);
        string fileName = Path.GetFileName(file);

        if (missingName)
            IssueRecorder?.AddNoName(request.Url, fileName);

        string? genre = null;
        if (config.GenreLookup && !missingName)
        {
            genre = await _genreLookup
                .LookupGenreAsync(naming.Track, naming.Artist, ct)
                .ConfigureAwait(false);
            // "Music" はジャンルとして意味を成さないため、取得できなかったものとして扱う。
            if (string.Equals(genre, "Music", StringComparison.OrdinalIgnoreCase))
                genre = null;
            if (genre is null)
                IssueRecorder?.AddNoGenre(request.Url, fileName);
        }

        string? hash = null;
        if (config.RecordAudioHash)
            hash = await CalculateAudioStreamHashAsync(file, config, ct).ConfigureAwait(false);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.EmbedMetadata)
        {
            // Match the Python implementation: the audio title becomes the
            // final filename, and track/artist are the cleaned values.
            metadata["title"] = naming.Filename;
            if (naming.Track.Length > 0)
                metadata["track"] = naming.Track;
            if (naming.Artist.Length > 0)
                metadata["artist"] = naming.Artist;
            if (naming.Cover && naming.AlbumArtist.Length > 0)
                metadata["album_artist"] = naming.AlbumArtist;
            if (!string.IsNullOrEmpty(genre))
                metadata["genre"] = genre;
        }

        if (!string.IsNullOrEmpty(hash))
            metadata["AUDIO_STREAM_SHA256"] = hash;

        if (metadata.Count == 0)
            return;

        await EmbedAudioMetadataAsync(file, metadata, config, ct).ConfigureAwait(false);
    }

    private static async Task<string?> CalculateAudioStreamHashAsync(
        string file,
        AppConfig config,
        CancellationToken ct)
    {
        string? ffmpeg = ResolveFfmpeg(config);
        if (ffmpeg is null)
            return null;

        var arguments = new List<string>
        {
            "-v", "error",
            "-i", file,
            "-map", "0:a:0",
            "-f", "streamhash",
            "-hash", "sha256",
            "-",
        };

        ProcessResult result = await RunProcessAsync(ffmpeg, arguments, null, ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            AppLogger.Warn(
                $"音声ストリームハッシュの計算に失敗しました: {file}: " +
                Tail(result.Stderr, 1000));
            return null;
        }

        Match match = StreamHashPattern.Match(result.Stdout);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static async Task EmbedAudioMetadataAsync(
        string file,
        IReadOnlyDictionary<string, string> metadata,
        AppConfig config,
        CancellationToken ct)
    {
        string? ffmpeg = ResolveFfmpeg(config);
        if (ffmpeg is null)
            return;

        string directory = Path.GetDirectoryName(file) ?? ".";
        string temporary = Path.Combine(
            directory,
            Path.GetRandomFileName() + Path.GetExtension(file));

        var arguments = new List<string>
        {
            "-v", "error",
            "-i", file,
            "-map", "0",
            "-codec", "copy",
        };

        foreach ((string key, string value) in metadata)
        {
            arguments.Add("-metadata");
            arguments.Add($"{key}={value.ReplaceLineEndings(" ")}");
        }

        arguments.Add("-y");
        arguments.Add(temporary);

        try
        {
            ProcessResult result = await RunProcessAsync(ffmpeg, arguments, null, ct)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                AppLogger.Warn(
                    $"音声メタデータの埋め込みに失敗しました: {file}: " +
                    Tail(result.Stderr, 1000));
                return;
            }

            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // ignore cleanup errors
            }
        }
    }

    private static void CleanupEmbedThumbnails(AppConfig config, string outputDir)
    {
        if (!config.EmbedAlbumArt || config.Thumbnails)
            return;

        foreach (string directory in new[] { outputDir, config.DownloadDir }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (string pattern in new[] { "*.jpg", "*.jpeg", "*.webp", "*.png" })
            {
                foreach (string file in Directory.EnumerateFiles(
                             directory,
                             pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"サムネイルの削除に失敗しました: {file}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static (string? PlaylistItem, string DownloadUrl) ResolvePlaylistItemSelection(
        string url)
    {
        Uri uri;
        List<KeyValuePair<string, string>> query;
        try
        {
            uri = new Uri(url);
            query = ParseQueryPairs(uri.Query);
        }
        catch
        {
            return (null, url);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in query)
            values[pair.Key] = pair.Value;

        if (!values.TryGetValue("v", out string? videoId)
            || string.IsNullOrEmpty(videoId)
            || !values.TryGetValue("list", out string? listId)
            || string.IsNullOrEmpty(listId))
        {
            return (null, url);
        }

        if (IsYouTubeMusicWatchUrl(uri))
        {
            AppLogger.Info(
                "YouTube Music の視聴URLが指定されたため、" +
                "v で指定された1曲のみダウンロードします。");
            return (null, SingleVideoUrl(url));
        }

        List<string> indexValues = query
            .Where(pair => pair.Key.Equals("index", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToList();

        if (indexValues.Count == 0)
            return (null, url);

        if (indexValues.Count != 1)
        {
            AppLogger.Warn(
                $"プレイリストの index が複数指定されています: " +
                $"{string.Join(",", indexValues)}。単体動画としてダウンロードします。");
            return (null, SingleVideoUrl(url));
        }

        string index = indexValues[0];
        if (!int.TryParse(index, out int parsed) || parsed < 1)
        {
            AppLogger.Warn(
                $"プレイリストの index が不正です: {index}。" +
                "単体動画としてダウンロードします。");
            return (null, SingleVideoUrl(url));
        }

        return (index, url);
    }

    private static List<KeyValuePair<string, string>> ParseQueryPairs(string query)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(query))
            return result;

        foreach (string part in query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string key = equals >= 0 ? part[..equals] : part;
            string value = equals >= 0 ? part[(equals + 1)..] : "";
            result.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(key),
                Uri.UnescapeDataString(value)));
        }

        return result;
    }

    private static string SingleVideoUrl(string url)
    {
        var uri = new Uri(url);
        List<KeyValuePair<string, string>> pairs = ParseQueryPairs(uri.Query)
            .Where(pair => !pair.Key.Equals("list", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Equals("index", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var builder = new UriBuilder(uri)
        {
            Query = string.Join(
                "&",
                pairs.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri.ToString();
    }

    private static bool IsYouTubeMusicWatchUrl(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        bool isMusicHost = host == "music.youtube.com"
            || host.EndsWith(".music.youtube.com", StringComparison.Ordinal);
        return isMusicHost
            && uri.AbsolutePath.TrimEnd('/').EndsWith("/watch", StringComparison.OrdinalIgnoreCase);
    }

    private static string? VideoIdFromUrl(string url)
    {
        Match match = Regex.Match(url, @"[?&]v=([^&#]+)");
        if (match.Success)
            return Uri.UnescapeDataString(match.Groups[1].Value);

        try
        {
            var uri = new Uri(url);
            if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                string id = uri.AbsolutePath.Trim('/');
                return id.Length > 0 ? id : null;
            }
        }
        catch
        {
            // ignore invalid URLs
        }

        return null;
    }

    private static string VideoIdFromFileName(string file)
    {
        Match match = Regex.Match(
            Path.GetFileNameWithoutExtension(file),
            @"\[([^\]]+)\]\s*$");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static bool ArchiveContains(AppConfig config, string? videoId)
    {
        if (string.IsNullOrEmpty(videoId) || !File.Exists(config.ArchiveFile))
            return false;

        try
        {
            foreach (string line in File.ReadLines(config.ArchiveFile, Encoding.UTF8))
            {
                if (line.Trim().Equals($"youtube {videoId}", StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            // ignore archive read errors
        }

        return false;
    }

    private static string ResolveYtDlp(AppConfig config)
    {
        string value = string.IsNullOrWhiteSpace(config.YtDlpLocation)
            ? "yt-dlp"
            : config.YtDlpLocation;

        if (Path.IsPathRooted(value) || value.Contains('\\') || value.Contains('/'))
        {
            string full = Path.GetFullPath(value);
            if (File.Exists(full))
                return full;
        }

        string appCandidate = Path.Combine(AppContext.BaseDirectory, value);
        if (File.Exists(appCandidate))
            return appCandidate;
        if (File.Exists(appCandidate + ".exe"))
            return appCandidate + ".exe";

        return value;
    }

    private static string? ResolveFfmpeg(AppConfig config)
    {
        string value = config.FfmpegLocation;
        if (Directory.Exists(value))
        {
            string exe = Path.Combine(value, "ffmpeg.exe");
            if (File.Exists(exe))
                return exe;
            string noExtension = Path.Combine(value, "ffmpeg");
            if (File.Exists(noExtension))
                return noExtension;
            AppLogger.Warn($"ffmpegの配置先が存在しません: {value}");
            return "ffmpeg";
        }

        if (File.Exists(value))
            return value;

        if (!value.Contains('\\') && !value.Contains('/') && !Path.IsPathRooted(value))
            return value;

        AppLogger.Warn($"ffmpegの配置先が存在しません: {value}");
        return "ffmpeg";
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<DownloadProgress>? progressCallback,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var files = new List<(string Id, string Path)>();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
                return;

            stdout.AppendLine(eventArgs.Data);
            if (eventArgs.Data.StartsWith("FILE:", StringComparison.Ordinal))
            {
                string payload = eventArgs.Data[5..].Trim();
                int separator = payload.IndexOf('|');
                files.Add(separator >= 0
                    ? (payload[..separator].Trim(), payload[(separator + 1)..].Trim())
                    : ("", payload));
            }
            else if (eventArgs.Data.Contains("[download]", StringComparison.Ordinal)
                     && eventArgs.Data.Contains('%'))
            {
                AppLogger.Info(eventArgs.Data.Trim());
                DownloadProgress? progress = ParseProgress(eventArgs.Data);
                if (progress is not null)
                    progressCallback?.Invoke(progress);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
                return;

            stderr.AppendLine(eventArgs.Data);
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                AppLogger.Warn(eventArgs.Data.Trim());
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"実行ファイルを起動できませんでした: {fileName}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch
            {
                // Best effort: the process may already have exited.
            }

            throw;
        }

        process.WaitForExit();

        return new ProcessResult(
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            files);
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => "",
        };
    }

    private static string? GetStringOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? "";
            return text.Length > 0 ? text : null;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            string text = string.Join(
                ", ",
                value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()));
            return text.Length > 0 ? text : null;
        }

        return null;
    }

    private static IReadOnlyList<string> FilterInfoArgs(IReadOnlyList<string> extraArgs)
    {
        IReadOnlyList<string> filtered = FilterExtraArgs(extraArgs);
        var result = new List<string>();
        for (int i = 0; i < filtered.Count; i++)
        {
            string token = filtered[i];
            string name = token.TrimStart('-');
            int equals = name.IndexOf('=');
            if (equals >= 0)
                name = name[..equals];

            if (NonJsonActions.Contains(name))
            {
                if (equals < 0 && i + 1 < filtered.Count
                    && !filtered[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }
                continue;
            }

            result.Add(token);
        }
        return result;
    }

    private static DownloadProgress? ParseProgress(string line)
    {
        Match match = ProgressPattern.Match(line);
        if (!match.Success)
            return null;

        double? percent = double.TryParse(
            match.Groups["pct"].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
                ? parsed
                : null;
        string speed = match.Groups["speed"].Success ? match.Groups["speed"].Value : "";
        string eta = match.Groups["eta"].Success ? match.Groups["eta"].Value : "";
        return new DownloadProgress(
            percent,
            speed.Length > 0 ? speed : null,
            eta.Length > 0 ? eta : null);
    }

    /// <summary>
    /// トレイ入力から渡された yt-dlp オプションのうち、内部的に
    /// 使用中で上書きすると動作が壊れるものだけを取り除く。
    /// </summary>
    private static IReadOnlyList<string> FilterExtraArgs(IReadOnlyList<string> extraArgs)
    {
        var result = new List<string>();
        for (int i = 0; i < extraArgs.Count; i++)
        {
            string token = extraArgs[i];
            string name = token.TrimStart('-');
            int equals = name.IndexOf('=');
            if (equals >= 0)
                name = name[..equals];

            if (ReservedOptions.Contains(name))
            {
                if (equals < 0 && i + 1 < extraArgs.Count
                    && !extraArgs[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }
                continue;
            }

            result.Add(token);
        }
        return result;
    }

    private static void Log403Diagnostics(string stderr, AppConfig config)
    {
        if (!stderr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            return;

        AppLogger.Error(
            "YouTubeからメディアデータを取得できませんでした（HTTP 403）。");
        if (config.ForceIpv4)
        {
            AppLogger.Error(
                "IPv4固定は有効です。yt-dlpの更新状況、動画の配信条件、" +
                "PO Token関連の要件、ネットワーク側の拒否を確認してください。");
        }
        else
        {
            AppLogger.Error(
                "IPv4固定は無効です。config.tomlのforce_ipv4 = trueを" +
                "試してください。");
        }
    }

    private static string Tail(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[^maxLength..];

    private sealed record MediaEntry(
        string Id,
        string Title,
        string Uploader,
        string? Track,
        string? Artist);

    private sealed record ExtractionResult(
        bool IsPlaylist,
        string Title,
        string Uploader,
        string Id,
        List<MediaEntry> Entries,
        int? PlaylistCount);

    private sealed record ProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        List<(string Id, string Path)> Files);
}
