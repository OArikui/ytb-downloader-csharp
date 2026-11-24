using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YtbDownloader.Tray;

/// <summary>
/// tray 実行中のダウンロードの問題点を記録する。
/// [1] ダウンロード失敗、[2] 音楽(アーティスト・曲名不明)、[3] ジャンル不明を、
/// リンクとファイル名の一覧として保存する。
/// 重複は認めず、1と2・3は重複しないが、2と3は重複してよい。
/// </summary>
public sealed class TrayIssueRecorder : IDisposable
{
    private sealed record Entry(string Link, string Name)
    {
        public string ToLine() =>
            string.IsNullOrWhiteSpace(Name) ? Link : $"{Link}\t{Name}";
    }

    private const string SectionFailed = "[1] ダウンロード失敗";
    private const string SectionNoName = "[2] 音楽(アーティスト・曲名不明)";
    private const string SectionNoGenre = "[3] ジャンル不明";

    private readonly string _path;
    private readonly object _gate = new();
    private List<Entry> _failed = [];
    private List<Entry> _noName = [];
    private List<Entry> _noGenre = [];
    private bool _loading;

    public TrayIssueRecorder(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// [1] ダウンロード失敗として記録する。
    /// </summary>
    public void AddFailed(string? link, string? name = null)
    {
        lock (_gate)
        {
            if (TryAdd(
                    _failed,
                    Keys(_failed, _noName, _noGenre),
                    link,
                    name)
                && !_loading)
                Save();
        }
    }

    /// <summary>
    /// [2] music としてアーティスト・曲名を取得できなかったものとして記録する。
    /// </summary>
    public void AddNoName(string? link, string? name = null)
    {
        lock (_gate)
        {
            if (TryAdd(
                    _noName,
                    Keys(_failed, _noName),
                    link,
                    name)
                && !_loading)
                Save();
        }
    }

    /// <summary>
    /// [3] ジャンルを取得できなかったものとして記録する。
    /// </summary>
    public void AddNoGenre(string? link, string? name = null)
    {
        lock (_gate)
        {
            if (TryAdd(
                    _noGenre,
                    Keys(_failed, _noGenre),
                    link,
                    name)
                && !_loading)
                Save();
        }
    }

    public void Dispose()
    {
        // 各追加時に Save() しているため、Dispose での追加は不要。
    }

    private static HashSet<string> Keys(params IReadOnlyList<List<Entry>> lists)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (List<Entry> list in lists)
            foreach (Entry entry in list)
                if (!string.IsNullOrWhiteSpace(entry.Link))
                    keys.Add(entry.Link);
        return keys;
    }

    private static bool TryAdd(
        List<Entry> target,
        HashSet<string> takenKeys,
        string? link,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(link))
            return false;
        if (!takenKeys.Add(link))
            return false;
        target.Add(new Entry(link, name ?? ""));
        return true;
    }

    private void Load()
    {
        lock (_gate)
        {
            _loading = true;
            try
            {
                if (!File.Exists(_path))
                    return;

                string? section = null;
                foreach (string rawLine in File.ReadLines(_path))
                {
                    string line = rawLine.TrimEnd('\r', '\n', ' ');
                    if (line.StartsWith("[1]", StringComparison.Ordinal))
                    {
                        section = SectionFailed;
                        continue;
                    }
                    if (line.StartsWith("[2]", StringComparison.Ordinal))
                    {
                        section = SectionNoName;
                        continue;
                    }
                    if (line.StartsWith("[3]", StringComparison.Ordinal))
                    {
                        section = SectionNoGenre;
                        continue;
                    }
                    if (section is null || line.Length == 0)
                        continue;

                    string link = line;
                    string name = "";
                    int tab = line.IndexOf('\t');
                    if (tab >= 0)
                    {
                        link = line[..tab];
                        name = line[(tab + 1)..];
                    }
                    if (link.Length == 0)
                        continue;

                    switch (section)
                    {
                        case SectionFailed:
                            AddFailed(link, name);
                            break;
                        case SectionNoName:
                            AddNoName(link, name);
                            break;
                        default:
                            AddNoGenre(link, name);
                            break;
                    }
                }
            }
            catch
            {
                // 読み込み失敗は致命ではない。リストは空の状態で継続する。
            }
            finally
            {
                _loading = false;
            }
        }
    }

    private void Save()
    {
        var sb = new StringBuilder();
        AppendSection(sb, SectionFailed, _failed);
        AppendSection(sb, SectionNoName, _noName);
        AppendSection(sb, SectionNoGenre, _noGenre);

        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(
                _path,
                sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // 記録失敗は致命ではない。
        }
    }

    private static void AppendSection(StringBuilder sb, string section, List<Entry> entries)
    {
        if (entries.Count == 0)
            return;
        sb.AppendLine(section).AppendLine();
        foreach (Entry entry in entries)
            sb.AppendLine(entry.ToLine());
        sb.AppendLine();
    }
}
