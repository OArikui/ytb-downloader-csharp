namespace YtbDownloader.Logging;

public static class LoggingSetup
{
    public static void Configure(string logDir)
    {
        AppLogger.Initialize(logDir);
    }
}
