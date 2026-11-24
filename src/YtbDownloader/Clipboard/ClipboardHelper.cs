using System.Text.RegularExpressions;

namespace YtbDownloader.Clipboard;

public static partial class ClipboardHelper
{
    // Matches youtube.com / music.youtube.com / youtu.be URLs.
    [GeneratedRegex(@"https?://(?:[a-z0-9-]+\.)*youtube\.com[^\s]*|https?://(?:[a-z0-9-]+\.)*youtu\.be[^\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YoutubeUrlRegex();

    private const string TrailingTrim = ".,;:!?\"'。、）】』（";

    public static string? GetClipboardText()
    {
        try
        {
            // The tray application runs on an STA thread, which is required
            // by the WPF clipboard APIs.
            if (System.Windows.Clipboard.ContainsText())
                return System.Windows.Clipboard.GetText();
        }
        catch
        {
            // ignore clipboard access errors
        }
        return null;
    }

    public static string? FindYoutubeUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = YoutubeUrlRegex().Match(text);
        if (!match.Success) return null;
        string url = match.Value.Trim();
        while (url.Length > 0 && TrailingTrim.Contains(url[^1]))
            url = url[..^1];
        return url.Length == 0 ? null : url;
    }
}
