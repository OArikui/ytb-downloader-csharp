namespace YtbDownloader.Cli;

public sealed record CliOptions
{
    public string? Url { get; init; }
    public string? Mode { get; init; } // video|audio
    public string? ConfigPath { get; init; }
    public string? LinkListPath { get; init; }
    public bool Tray { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public string? Error { get; init; }

    /// <summary>トレイ起動時に yt-dlp へ渡すオプション。</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    public static string HelpText =>
        """
        YTB downloader (C#)
        Usage:
          YtbDownloader [URL] [--mode audio|video] [--config PATH] [--linklist PATH] [--tray]
          YtbDownloader --help
          YtbDownloader --version

        Options:
          --mode audio|video   Download mode (default: config default_mode)
          --config PATH        config.toml path (default: config.toml next to the exe)
          --linklist PATH      JSON link list (array of URL strings or {url, mode})
          --tray               Start the tray application
          --help, -h           Show this help
          --version            Show version

        Tray mode:
          Unknown long options (e.g. --list-formats, --proxy URL) are passed
          through to yt-dlp for every queued download.
          The input box also accepts: <URL> [--mode audio|video|--audio|--video]
          followed by yt-dlp options such as --list-formats.
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg is "--help" or "-h")
            {
                options = options with { ShowHelp = true };
                continue;
            }
            if (arg is "--version" or "-V")
            {
                options = options with { ShowVersion = true };
                continue;
            }
            if (arg == "--tray")
            {
                options = options with { Tray = true };
                continue;
            }
            if (arg == "--mode")
            {
                if (i + 1 < args.Length)
                    options = options with { Mode = args[++i] };
                else
                    options = options with { Error = "--mode の値がありません。" };
                continue;
            }
            if (arg.StartsWith("--mode=", StringComparison.Ordinal))
            {
                options = options with { Mode = arg["--mode=".Length..] };
                continue;
            }
            if (arg == "--config")
            {
                if (i + 1 < args.Length)
                    options = options with { ConfigPath = args[++i] };
                else
                    options = options with { Error = "--config の値がありません。" };
                continue;
            }
            if (arg.StartsWith("--config=", StringComparison.Ordinal))
            {
                options = options with { ConfigPath = arg["--config=".Length..] };
                continue;
            }
            if (arg == "--linklist")
            {
                if (i + 1 < args.Length)
                    options = options with { LinkListPath = args[++i] };
                else
                    options = options with { Error = "--linklist の値がありません。" };
                continue;
            }
            if (arg.StartsWith("--linklist=", StringComparison.Ordinal))
            {
                options = options with { LinkListPath = arg["--linklist=".Length..] };
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // トレイモードでは未知の long option を yt-dlp へ渡す。
                var extras = options.ExtraArgs.ToList();
                extras.Add(arg);
                if (i + 1 < args.Length
                    && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                    && (!LooksLikeUrl(args[i + 1]) || options.Tray))
                {
                    extras.Add(args[++i]);
                }
                options = options with { ExtraArgs = extras };
                continue;
            }
            if (arg.StartsWith('-'))
            {
                options = options with { Error = $"不明なオプションです: {arg}" };
                continue;
            }

            if (options.Url is null)
                options = options with { Url = arg };
            else
                options = options with { Error = "URLが複数指定されています。" };
        }

        if (options.Url is not null && options.LinkListPath is not null)
            options = options with { Error = "URLと --linklist は同時に指定できません。" };
        if (options.Mode is not null && options.Mode is not ("audio" or "video"))
            options = options with { Error = $"不正なモードです:{options.Mode}" };
        if (options.ExtraArgs.Count > 0
            && (options.Url is not null || options.LinkListPath is not null))
        {
            options = options with
            {
                Error = $"不明なオプションです: {options.ExtraArgs[0]}",
            };
        }

        return options;
    }

    private static bool LooksLikeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || value.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || value.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
}
