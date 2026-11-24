using System.IO;
using System.Text;

namespace YtbDownloader.Logging;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static string? _logFile;

    public static string? LogFile
    {
        get
        {
            lock (Sync) return _logFile;
        }
    }

    public static void Initialize(string logDir)
    {
        lock (Sync)
        {
            if (_writer is not null)
                return;

            Directory.CreateDirectory(logDir);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logFile = Path.Combine(logDir, $"ytbdl_{timestamp}.log");
            _writer = new StreamWriter(
                _logFile,
                append: true,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Sync)
        {
            _writer?.WriteLine(line);
        }

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // No console is attached (WinExe); file logging is still active.
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
