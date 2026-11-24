using System.Windows.Threading;
using YtbDownloader.Clipboard;
using YtbDownloader.Config;
using YtbDownloader.Queue;
using WinForms = System.Windows.Forms;

namespace YtbDownloader.Tray;

public sealed class TrayApp : IDisposable
{
    private readonly AppConfig _config;
    private readonly DownloadQueue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly List<(ToastWindow Toast, DownloadRequest? Request)> _toasts = [];
    private WinForms.NotifyIcon? _notifyIcon;
    private ControlWindow? _controlWindow;
    private Action<QueueStatus>? _statusHandler;
    private bool _disposed;

    public TrayApp(AppConfig config, DownloadQueue queue, Dispatcher dispatcher)
    {
        _config = config;
        _queue = queue;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "YTB Downloader",
            Icon = System.Drawing.SystemIcons.Application,
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
        var (url, mode) = ParseStackedInput(text);
        if (url is null)
        {
            ShowToast("YouTube URLを入力してください。");
            return;
        }
        var req = _queue.Enqueue(url, mode ?? _config.DefaultMode, priority);
        string label = priority > 0 ? "即実行しました" : "スタックしました";
        ShowToast($"{label}: {DownloadRequest.Describe(req)}", req);
    }

    public static (string? Url, string? Mode) ParseStackedInput(string text)
    {
        string? url = ClipboardHelper.FindYoutubeUrl(text);
        if (url is null) return (null, null);

        string? mode = null;
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "--mode")
            {
                if (i + 1 < tokens.Length && tokens[i + 1] is "audio" or "video")
                    mode = tokens[i + 1];
            }
            else if (tokens[i].StartsWith("--mode=", StringComparison.Ordinal))
            {
                string value = tokens[i]["--mode=".Length..];
                if (value is "audio" or "video") mode = value;
            }
        }

        return (url, mode);
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

    private void ApplyStatus(QueueStatus status)
    {
        _controlWindow?.RefreshStatus(status);

        if (status.Running is not null)
        {
            foreach ((ToastWindow toast, DownloadRequest? request) in _toasts)
            {
                if (ReferenceEquals(request, status.Running))
                    toast.Update($"実行中: {DownloadRequest.Describe(status.Running)}");
            }
        }

        if (_notifyIcon is null)
            return;

        string tip = status.Running is not null
            ? $"YTB Downloader: {DownloadRequest.Describe(status.Running)}"
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
