using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YtbDownloader.Logging;

namespace YtbDownloader.Core;

public sealed class MusicBrainzGenreLookup
{
    private const string ApiBase = "https://musicbrainz.org/ws/2";
    private const string UserAgent = "YTB-downloader/0.1.9 (MusicBrainz genre lookup)";
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1);

    private static readonly HttpClient Http = CreateClient();
    private static readonly object RateLock = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<string?> LookupGenreAsync(
        string track,
        string artist,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(artist))
            return null;

        string cacheKey = $"{track}\u001f{artist}";
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out string? cached))
                return cached;
        }

        try
        {
            string query = $"recording:\"{track}\" AND artistname:\"{artist}\"";
            string searchUrl =
                $"{ApiBase}/recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

            using JsonDocument search = await GetJsonAsync(searchUrl, ct).ConfigureAwait(false);
            foreach (string recordingId in RecordingIds(search.RootElement))
            {
                string detailUrl =
                    $"{ApiBase}/recording/{Uri.EscapeDataString(recordingId)}?fmt=json&inc=genres+releases";
                using JsonDocument detail = await GetJsonAsync(detailUrl, ct).ConfigureAwait(false);
                string? genre = await RecordingGenreAsync(detail.RootElement, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(genre))
                {
                    lock (_cache) _cache[cacheKey] = genre;
                    return genre;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"MusicBrainz のジャンル照合に失敗しました: {ex.Message}");
        }

        lock (_cache) _cache[cacheKey] = null;
        return null;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);
        using HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (RateLock)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan elapsed = now - _lastRequestUtc;
            wait = elapsed < MinRequestInterval ? MinRequestInterval - elapsed : TimeSpan.Zero;
            _lastRequestUtc = now + wait;
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> RecordingIds(JsonElement root)
    {
        if (!root.TryGetProperty("recordings", out JsonElement recordings)
            || recordings.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        int count = 0;
        foreach (JsonElement recording in recordings.EnumerateArray())
        {
            if (count++ >= 5)
                yield break;

            if (recording.ValueKind == JsonValueKind.Object
                && recording.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(id.GetString()))
            {
                yield return id.GetString()!;
            }
        }
    }

    private static async Task<string?> RecordingGenreAsync(
        JsonElement detail,
        CancellationToken ct)
    {
        List<string> genres = SortedGenres(detail, "genres");
        if (genres.Count > 0)
            return genres[0];

        if (detail.TryGetProperty("releases", out JsonElement releases)
            && releases.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement release in releases.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || !release.TryGetProperty("release-group", out JsonElement group)
                    || group.ValueKind != JsonValueKind.Object
                    || !group.TryGetProperty("id", out JsonElement groupId)
                    || groupId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string id = groupId.GetString() ?? "";
                if (id.Length == 0)
                    continue;

                using JsonDocument groupDetail = await GetJsonAsync(
                    $"{ApiBase}/release-group/{Uri.EscapeDataString(id)}?inc=genres&fmt=json",
                    ct).ConfigureAwait(false);
                genres = SortedGenres(groupDetail.RootElement, "genres");
                if (genres.Count > 0)
                    return genres[0];
            }
        }

        if (detail.TryGetProperty("artist-credit", out JsonElement credits)
            && credits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement credit in credits.EnumerateArray())
            {
                if (credit.ValueKind != JsonValueKind.Object
                    || !credit.TryGetProperty("artist", out JsonElement artist)
                    || artist.ValueKind != JsonValueKind.Object
                    || !artist.TryGetProperty("id", out JsonElement artistId)
                    || artistId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string id = artistId.GetString() ?? "";
                if (id.Length == 0)
                    continue;

                using JsonDocument artistDetail = await GetJsonAsync(
                    $"{ApiBase}/artist/{Uri.EscapeDataString(id)}?inc=genres&fmt=json",
                    ct).ConfigureAwait(false);
                genres = SortedGenres(artistDetail.RootElement, "genres");
                if (genres.Count > 0)
                    return genres[0];

                List<string> tags = SortedGenres(artistDetail.RootElement, "tags");
                if (tags.Count > 0)
                    return tags[0];
            }
        }

        return null;
    }

    /// <summary>
    /// ジャンルとして意味を成さない汎用名。Python 版の実データで
    /// MusicBrainz の "Music" が最上位に来ることがあるため除外する。
    /// 除外された場合、他に候補がなければ「ジャンル不明」として記録される。
    /// </summary>
    private static bool IsMeaninglessGenre(string name) =>
        string.Equals(name.Trim(), "Music", StringComparison.OrdinalIgnoreCase);

    private static List<string> SortedGenres(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<(string Name, int Count)>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            int count = 0;
            if (item.TryGetProperty("count", out JsonElement countElement)
                && countElement.ValueKind == JsonValueKind.Number
                && countElement.TryGetInt32(out int parsed))
            {
                count = parsed;
            }

            string genreName = name.GetString() ?? "";
            if (IsMeaninglessGenre(genreName))
                continue;

            values.Add((genreName, count));
        }

        values.Sort((a, b) => b.Count.CompareTo(a.Count));
        return values.Select(value => value.Name).ToList();
    }
}
