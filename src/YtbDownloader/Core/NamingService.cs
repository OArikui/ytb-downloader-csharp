using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YtbDownloader.Core;

public sealed record AudioName(
    string Filename,
    string Track,
    string Artist,
    string AlbumArtist,
    bool Cover);

public static class NamingService
{
    private static readonly Regex InvalidFilenameChars = new(
        @"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(
        @"\s+", RegexOptions.Compiled);

    private static readonly Regex FeatPattern = new(
        @"\b(?:feat(?:uring)?\.?|ft\.?)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RemixPattern = new(
        @"\(([^()]*(?:remix|mix)[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverPattern = new(
        @"歌ってみた|cover|カバー",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverByPattern = new(
        @"(?:covered\s+by|cover\s+by)\s*[:：]?\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverSungPattern = new(
        @"歌ってみた\s*[:：]\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArtistSeparator = new(
        @"\s+-\s+", RegexOptions.Compiled);

    private static readonly Regex TrailingNoise = new(
        @"\s*[\[(（【].*?(?:official|music video|lyric video|lyrics|mv|pv|高音質|公式|オフィシャル|歌詞).*?[\])）】]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingBracket = new(
        @"^[【\[(（].*?[】\])）]\s*", RegexOptions.Compiled);

    private static readonly Regex TrailingOfficial = new(
        @"\s*[-/|]\s*(?:official|lyrics?|music video|mv)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverBySplit = new(
        @"\s*[/|｜]?\s*(?:covered|cover)\s+by\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverBracket = new(
        @"[\[(（【]\s*(?:歌ってみた|cover|カバー)\s*[\])）】]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverTrailing = new(
        @"\s+(?:歌ってみた|カバー)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoverArtistSplit = new(
        @"\s*[/|｜]\s*|\s+-\s+", RegexOptions.Compiled);

    private static readonly Regex CoverArtistLeadingTrim = new(
        @"^[\[【（(\])）】\s]+", RegexOptions.Compiled);

    private static readonly Regex CoverArtistTrailingTrim = new(
        @"[\])）】]+$", RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        },
        StringComparer.OrdinalIgnoreCase);

    public static string SanitizeFilename(string value)
    {
        value = (value ?? "").Normalize(NormalizationForm.FormKC);
        value = InvalidFilenameChars.Replace(value, "_");
        value = Whitespace.Replace(value, " ");
        value = value.Trim(' ', '.');
        if (value.Length == 0)
            value = "untitled";

        string stem = value.Split('.')[0];
        if (ReservedNames.Contains(stem))
            value = "_" + value;
        return value;
    }

    public static string NormalizeFeat(string text)
    {
        text = FeatPattern.Replace(text, "feat. ");
        text = Whitespace.Replace(text, " ");
        return text.Trim();
    }

    public static (string Track, string Feat) ExtractFeat(string text)
    {
        text = NormalizeFeat(text);
        Match match = Regex.Match(
            text,
            @"\bfeat\.\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return (text.Trim(), "");

        string featArtists = match.Groups[1].Value.Trim();
        string track = text[..match.Index].Trim();
        if (featArtists.StartsWith('('))
            featArtists = featArtists[1..];
        if (featArtists.EndsWith(')'))
            featArtists = featArtists[..^1];
        return (track, $"(feat. {featArtists})");
    }

    public static (string Track, List<string> Remixes) ExtractRemixes(string text)
    {
        var remixes = RemixPattern.Matches(text)
            .Select(match => $"({match.Groups[1].Value.Trim()})")
            .ToList();
        string remaining = RemixPattern.Replace(text, "");
        remaining = Whitespace.Replace(remaining, " ").Trim();
        return (remaining, remixes);
    }

    public static bool IsCover(string title) => CoverPattern.IsMatch(title ?? "");

    private static string CleanTrackTitle(string title)
    {
        title = TrailingNoise.Replace(title, "");
        title = LeadingBracket.Replace(title, "");
        title = TrailingOfficial.Replace(title, "");
        return Whitespace.Replace(title, " ").Trim(' ', '-', '/', '|');
    }

    private static string ExtractCoverArtist(string title, string uploader)
    {
        Match match = CoverByPattern.Match(title);
        if (!match.Success)
            match = CoverSungPattern.Match(title);

        if (match.Success)
        {
            string artist = match.Groups[1].Value.Trim();
            Match split = CoverArtistSplit.Match(artist);
            if (split.Success)
                artist = artist[..split.Index];

            artist = CoverArtistLeadingTrim.Replace(artist, "");
            artist = CoverArtistTrailingTrim.Replace(artist, "").Trim();
            if (artist.Length > 0)
                return artist;
        }

        return (uploader ?? "").Trim();
    }

    private static (string Track, string Artist) ParseTitle(string title, string uploader)
    {
        string clean = CleanTrackTitle(title);
        if (clean.Length == 0)
            return ("", "");

        if (IsCover(title))
        {
            string artist = ExtractCoverArtist(title, uploader);
            string track = clean;

            Match split = CoverBySplit.Match(track);
            if (split.Success)
                track = track[..split.Index];

            track = CoverBracket.Replace(track, "");
            track = CoverTrailing.Replace(track, "");
            track = Whitespace.Replace(track, " ").Trim(' ', '-', '/', '|');
            if (track.Length == 0)
                track = clean;
            return (track, artist);
        }

        Match separator = ArtistSeparator.Match(clean);
        if (separator.Success)
        {
            string first = clean[..separator.Index].Trim();
            string second = clean[(separator.Index + separator.Length)..].Trim();
            if (first.Length > 0 && second.Length > 0)
                return (second, first); // 「アーティスト - 曲名」を優先
        }

        return (clean, (uploader ?? "").Trim());
    }

    public static AudioName BuildAudioName(
        string title,
        string uploader,
        string? metadataTrack,
        string? metadataArtist)
    {
        title = (title ?? "").Trim();
        uploader = (uploader ?? "").Trim();
        metadataTrack = (metadataTrack ?? "").Trim();
        metadataArtist = (metadataArtist ?? "").Trim();

        bool cover = IsCover(title);
        string originalArtist = metadataArtist;

        string track;
        string artist;
        if (metadataTrack.Length > 0 && metadataArtist.Length > 0)
        {
            track = metadataTrack;
            artist = metadataArtist;
        }
        else
        {
            (track, artist) = ParseTitle(title, uploader);
        }

        if (metadataTrack.Length > 0)
            track = metadataTrack;
        if (metadataArtist.Length > 0 && !cover)
            artist = metadataArtist;
        if (cover)
            artist = ExtractCoverArtist(title, uploader);

        if (track.Length == 0)
        {
            return new AudioName(
                SanitizeFilename(title),
                title,
                artist,
                originalArtist,
                cover);
        }

        track = CleanTrackTitle(track);
        (track, var remixes) = ExtractRemixes(track);
        (track, string feat) = ExtractFeat(track);

        var parts = new List<string> { track };
        if (feat.Length > 0)
            parts.Add(feat);
        parts.AddRange(remixes);
        if (cover)
            parts.Add("(Cover)");

        string filename = string.Join(" ", parts.Where(part => part.Length > 0));
        if (artist.Length > 0)
            filename = $"{filename} - {artist}";

        return new AudioName(
            SanitizeFilename(filename),
            track,
            artist,
            originalArtist,
            cover);
    }

    public static string BuildVideoName(string title, string id)
    {
        title = string.IsNullOrEmpty(title) ? "untitled" : title;
        id = string.IsNullOrEmpty(id) ? "unknown" : id;
        return SanitizeFilename($"{title} [{id}]");
    }
}
