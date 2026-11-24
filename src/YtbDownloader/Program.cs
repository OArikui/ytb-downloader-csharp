using System.IO;
using System.Threading;
using YtbDownloader.Cli;
using YtbDownloader.Config;
using YtbDownloader.Core;
using YtbDownloader.Logging;
using YtbDownloader.Queue;

namespace YtbDownloader;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            CliOptions options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }
            if (options.ShowVersion)
            {
                Console.WriteLine($"YTB downloader {VersionInfo.AppVersion}");
                return 0;
            }
            if (options.Error is not null)
            {
                Console.Error.WriteLine(options.Error);
                return 2;
            }

            string appDir = AppContext.BaseDirectory;
            string configPath = options.ConfigPath ?? Path.Combine(appDir, "config.toml");
            AppConfig config = ConfigLoader.Load(configPath, appDir);
            LoggingSetup.Configure(config.LogDir);
            bool isTray = string.IsNullOrWhiteSpace(options.Url)
                && options.LinkListPath is null;
            AppLogger.Info(
                $"YTB downloader v{VersionInfo.AppVersion} started." +
                (isTray ? " tray" : ""));

            if (isTray)
            {
                if (!OperatingSystem.IsWindows())
                {
                    AppLogger.Error("URLまたは --linklist を指定してください。");
                    return 2;
                }

                var backend = new YtDlpBackend();
                var queue = new DownloadQueue(config, backend);
                var app = new App
                {
                    BootstrapConfig = config,
                    BootstrapQueue = queue,
                };
                app.InitializeComponent();
                return app.Run();
            }

            var downloadBackend = new YtDlpBackend();

            if (options.LinkListPath is not null)
            {
                IReadOnlyList<LinkListItem> items = LinkListLoader.Load(options.LinkListPath);
                int success = 0;
                int skipped = 0;
                int failed = 0;

                foreach (LinkListItem item in items)
                {
                    string? url = ResolveUrl(item.Url);
                    string mode = item.Mode ?? options.Mode ?? config.DefaultMode;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(url))
                            throw new InvalidOperationException("URLが指定されていません。");
                        if (mode is not ("audio" or "video"))
                            throw new InvalidOperationException($"不正なモードです:{mode}");

                        if (YtDlpBackend.IsArchivedUrl(url, config))
                        {
                            AppLogger.Info($"アーカイブ済みのためスキップします: {url}");
                            skipped++;
                            continue;
                        }

                        DownloadRequest request = RunDownload(url, mode, config, downloadBackend);
                        if (request.Skipped)
                            skipped++;
                        else
                            success++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AppLogger.Exception(
                            $"リンクリストの処理に失敗しました: {url}", ex);
                    }
                }

                AppLogger.Info(
                    $"リンクリスト処理結果: 成功={success}, " +
                    $"スキップ={skipped}, 失敗={failed}");
                return failed > 0 ? 1 : 0;
            }

            string? singleUrl = ResolveUrl(options.Url);
            if (string.IsNullOrWhiteSpace(singleUrl))
            {
                AppLogger.Error("URLを指定してください。");
                return 2;
            }

            string singleMode = options.Mode ?? config.DefaultMode;
            if (singleMode is not ("audio" or "video"))
            {
                AppLogger.Error($"不正なモードです:{singleMode}");
                return 2;
            }

            RunDownload(singleUrl, singleMode, config, downloadBackend);
            AppLogger.Info("処理が終了しました。");
            return 0;
        }
        catch (Exception ex)
        {
            AppLogger.Exception("処理中にエラーが発生しました。", ex);
            return 1;
        }
        finally
        {
            AppLogger.Shutdown();
        }
    }

    private static DownloadRequest RunDownload(
        string url,
        string mode,
        AppConfig config,
        YtDlpBackend backend)
    {
        var request = new DownloadRequest
        {
            Url = url,
            Mode = mode,
            Description = url,
        };

        backend.DownloadAsync(
            request,
            config,
            info => AppLogger.Info(
                $"情報: {info.Kind ?? "unknown"} | {info.Title} [{info.VideoId}]"),
            CancellationToken.None).GetAwaiter().GetResult();

        return request;
    }

    private static string? ResolveUrl(string? cliUrl)
    {
        if (!string.IsNullOrWhiteSpace(cliUrl))
            return cliUrl;
        return Environment.GetEnvironmentVariable("YTB_DOWNLOADER_URL");
    }
}
