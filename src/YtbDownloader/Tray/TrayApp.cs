using System.IO;
using System.Windows.Threading;
using YtbDownloader.Clipboard;
using YtbDownloader.Config;
using YtbDownloader.Queue;
using WinForms = System.Windows.Forms;

namespace YtbDownloader.Tray;

/// <summary>トレイの入力欄を解析した結果。</summary>
public sealed record TrayInput(
    string? Url,
    string? Mode,
    IReadOnlyList<string> ExtraArgs);

public sealed class TrayApp : IDisposable
{
    private readonly AppConfig _config;
    private readonly DownloadQueue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyList<string> _defaultExtraArgs;
    private readonly List<(ToastWindow Toast, DownloadRequest? Request)> _toasts = [];
    private WinForms.NotifyIcon? _notifyIcon;
    private ControlWindow? _controlWindow;
    private Action<QueueStatus>? _statusHandler;
    private bool _disposed;

    public TrayApp(
        AppConfig config,
        DownloadQueue queue,
        Dispatcher dispatcher,
        IReadOnlyList<string>? defaultExtraArgs = null)
    {
        _config = config;
        _queue = queue;
        _dispatcher = dispatcher;
        _defaultExtraArgs = defaultExtraArgs ?? [];
    }

    public void Start()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "YTB Downloader",
            Icon = LoadTrayIcon(),
            Visible = true,
        };
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                OnClipboardDownload();
            else if (e.Button == WinForms.MouseButtons.Right)
                ShowControlWindow();
        };

        _controlWindow = new ControlWindow(this);
        _statusHandler = status =>
            _dispatcher.BeginInvoke(() => ApplyStatus(status));
        _queue.StatusChanged += _statusHandler;
        _queue.Start();
    }

    public DownloadQueue Queue => _queue;
    public AppConfig Config => _config;

    public void EnqueueFromInput(string text, int priority = 0)
    {
        TrayInput input = ParseStackedInput(text);
        if (input.Url is null)
        {
            ShowToast("YouTube URLを入力してください。");
            return;
        }

        var extraArgs = new List<string>(_defaultExtraArgs);
        extraArgs.AddRange(input.ExtraArgs);
        var req = _queue.Enqueue(
            input.Url,
            input.Mode ?? _config.DefaultMode,
            priority,
            extraArgs);
        string label = priority > 0 ? "即実行しました" : "スタックしました";
        ShowToast($"{label}: {DownloadRequest.Describe(req)}", req);
    }

    /// <summary>
    /// 入力形式: &lt;YouTube URL&gt; [--mode audio|video|--audio|--video]
    /// [--yt-dlpオプション ...]
    /// --mode / --audio / --video 以外のオプションは yt-dlp へそのまま渡す。
    /// </summary>
    public static TrayInput ParseStackedInput(string text)
    {
        string? url = ClipboardHelper.FindYoutubeUrl(text);
        if (url is null) return new TrayInput(null, null, []);

        string? mode = null;
        var extraArgs = new List<string>();
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (string.Equals(token, url, StringComparison.Ordinal))
                continue;

            if (token == "--mode")
            {
                if (i + 1 < tokens.Length && tokens[i + 1] is "audio" or "video")
                    mode = tokens[++i];
                continue;
            }
            if (token.StartsWith("--mode=", StringComparison.Ordinal))
            {
                string value = token["--mode=".Length..];
                if (value is "audio" or "video") mode = value;
                continue;
            }
            if (token is "--audio" or "--video")
            {
                mode = token == "--audio" ? "audio" : "video";
                continue;
            }

            if (!token.StartsWith("-", StringComparison.Ordinal))
                continue;

            extraArgs.Add(token);
            // --proxy URL や --playlist-items 1-3 のような値を持つオプション。
            if (!token.Contains('=')
                && i + 1 < tokens.Length)
            {
                string next = tokens[i + 1];
                bool isValue = !next.StartsWith("-", StringComparison.Ordinal)
                    && !string.Equals(next, url, StringComparison.Ordinal);
                if (isValue)
                    extraArgs.Add(tokens[++i]);
            }
        }

        return new TrayInput(url, mode, extraArgs);
    }

    public void ClearQueue()
    {
        int count = _queue.Clear();
        ShowToast(count > 0 ? $"キューを消去しました({count}件)" : "キューに未処理のものはありません");
    }

    public void ShowControlWindow()
    {
        if (_controlWindow is null) return;
        _controlWindow.Show();
        _controlWindow.Activate();
        _controlWindow.RefreshStatus(_queue.Status());
    }

    public void ShowToast(string text, DownloadRequest? req = null)
    {
        double controlHeight = _controlWindow?.ActualHeight ?? 0;
        if (controlHeight <= 0)
            controlHeight = 190;
        var toast = new ToastWindow(text, controlHeight);
        var entry = (toast, req);
        _toasts.Add(entry);
        toast.Closed += (_, _) => _toasts.Remove(entry);
        toast.Show();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "favicon.ico");
        if (File.Exists(path))
            return new System.Drawing.Icon(path);
        return System.Drawing.SystemIcons.Application;
    }

    private void OnClipboardDownload()
    {
        string? text = ClipboardHelper.GetClipboardText();
        if (ClipboardHelper.FindYoutubeUrl(text) is null)
        {
            ShowToast("クリップボードにYouTube URLをコピーしてください。");
            return;
        }
        EnqueueFromInput(text ?? "");
    }

    /// <summary>実行中項目の進捗・速度を含む表示文字列。</summary>
    public static string DescribeRunning(DownloadRequest req)
    {
        string text = DownloadRequest.Describe(req);
        if (req.Progress is null)
            return text;

        var parts = new List<string>();
        if (req.Progress.Percent is double percent)
            parts.Add($"{percent:F1}%");
        if (!string.IsNullOrEmpty(req.Progress.Speed))
            parts.Add(req.Progress.Speed);
        if (!string.IsNullOrEmpty(req.Progress.Eta))
            parts.Add($"ETA {req.Progress.Eta}");
        return parts.Count > 0 ? $"{text} | {string.Join(" ", parts)}" : text;
    }

    private void ApplyStatus(QueueStatus status)
    {
        _controlWindow?.RefreshStatus(status);

        if (status.Running is not null)
        {
            foreach ((ToastWindow toast, DownloadRequest? request) in _toasts)
            {
                if (ReferenceEquals(request, status.Running))
                    toast.Update($"実行中: {DescribeRunning(status.Running)}");
            }
        }

        if (_notifyIcon is null)
            return;

        string tip = status.Running is not null
            ? $"YTB Downloader: {DescribeRunning(status.Running)} " +
              $"[{status.CompletedCount}完了/{status.Queued.Count}待ち]"
            : status.Queued.Count > 0
                ? $"YTB Downloader: キュー済み({status.Queued.Count}件)"
                : "YTB Downloader";
        _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon.Text limit
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_statusHandler is not null)
        {
            _queue.StatusChanged -= _statusHandler;
            _statusHandler = null;
        }

        _queue.Stop();

        var toasts = _toasts.ToArray();
        _toasts.Clear();
        foreach (var (toast, _) in toasts)
            toast.Close();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _controlWindow?.ClosePermanently();
        _controlWindow = null;
    }
}
