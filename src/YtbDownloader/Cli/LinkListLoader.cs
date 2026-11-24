using System.IO;
using System.Text.Json;

namespace YtbDownloader.Cli;

public sealed record LinkListItem(string Url, string? Mode);

public static class LinkListLoader
{
    public static IReadOnlyList<LinkListItem> Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"リンクリストの読み込みに失敗しました: {path}: {ex.Message}", ex);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"リンクリストの読み込みに失敗しました: {path}: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("リンクリストは配列で指定してください");

            return Parse(doc.RootElement);
        }
    }

    private static IReadOnlyList<LinkListItem> Parse(JsonElement root)
    {
        var result = new List<LinkListItem>();
        int index = 0;
        foreach (JsonElement item in root.EnumerateArray())
        {
            index++;
            if (item.ValueKind == JsonValueKind.String)
            {
                string? url = item.GetString();
                if (string.IsNullOrEmpty(url))
                    throw new InvalidOperationException($"リンクリスト {index} 件目の URL が空です");
                result.Add(new LinkListItem(url, null));
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("url", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(urlProp.GetString()))
            {
                string? mode = null;
                if (item.TryGetProperty("mode", out var modeProp)
                    && modeProp.ValueKind != JsonValueKind.Null)
                {
                    if (modeProp.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            $"リンクリスト {index} 件目の mode が不正です: {modeProp}");
                    }

                    mode = modeProp.GetString();
                    if (mode is not ("audio" or "video"))
                    {
                        throw new InvalidOperationException(
                            $"リンクリスト {index} 件目の mode が不正です: {mode}");
                    }
                }

                result.Add(new LinkListItem(urlProp.GetString()!, mode));
                continue;
            }

            throw new InvalidOperationException($"リンクリスト {index} 件目に url がありません");
        }

        return result;
    }
}
