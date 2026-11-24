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

        return options;
    }
}
