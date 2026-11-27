using YtbDownloader.Config;
using YtbDownloader.Core;
using YtbDownloader.Logging;
using YtbDownloader.Tray;

namespace YtbDownloader.Queue;

public sealed class DownloadQueue
{
    private readonly AppConfig _config;
    private readonly IDownloadBackend _backend;
    private readonly object _lock = new();
    private readonly List<DownloadRequest> _queue = [];
    private readonly AutoResetEvent _newItems = new(false);
    private readonly SemaphoreSlim _probeSlots = new(3);
    private CancellationTokenSource _probeCts = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _paused;
    private DownloadRequest? _running;
    private long _nextSequence;
    private int _completed;
    private DateTime _lastProgressEmitUtc = DateTime.MinValue;

    /// <summary> tray 実行中の問題点記録用。</summary>
    public TrayIssueRecorder? IssueRecorder { get; set; }

    public event Action<QueueStatus>? StatusChanged;

    public DownloadQueue(AppConfig config, IDownloadBackend backend)
    {
        _config = config;
        _backend = backend;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_worker is not null) return;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerLoop(_cts.Token));
        }
    }

    public void Stop(TimeSpan? timeout = null)
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_lock)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }
        cts?.Cancel();
        _probeCts.Cancel();
        _probeCts = new CancellationTokenSource();
        _newItems.Set();
        bool stopped = worker?.Wait(timeout ?? TimeSpan.FromSeconds(3)) ?? true;
        if (stopped)
            cts?.Dispose();
    }

    public DownloadRequest Enqueue(
        string url,
        string mode,
        int priority = 0,
        IReadOnlyList<string>? extraArgs = null)
    {
        var req = new DownloadRequest
        {
            Url = url,
            Mode = mode,
            Priority = priority,
            Description = url,
            ExtraArgs = extraArgs ?? [],
            Sequence = Interlocked.Increment(ref _nextSequence),
        };
        lock (_lock)
        {
            if (priority > 0) _queue.Insert(0, req);
            else _queue.Add(req);
        }
        EmitStatus();
        _newItems.Set();

        // キューに積まれた全タスクのタイトルを、ダウンロードとは別に先行取得する。
        _ = ResolveTitleAsync(req);
        return req;
    }

    public DownloadRequest? PopNext()
    {
        lock (_lock)
        {
            if (_paused || _queue.Count == 0) return null;

            // 単動画は常にプレイリストより先に処理する。
            // 未取得(サイズ不明)のものは単動画とプレイリストの間に入れる。
            DownloadRequest item = _queue
                .OrderBy(x => x.Priority > 0 ? 0 : 1)
                .ThenBy(x => KindRank(x.Kind))
                .ThenBy(x => x.Sequence)
                .First();
            _queue.Remove(item);
            _running = item;
            return item;
        }
    }

    private static int KindRank(string? kind) => kind switch
    {
        "single" => 0,
        null => 1,
        _ => 2,
    };

    public int Clear()
    {
        int count;
        lock (_lock)
        {
            count = _queue.Count;
            _queue.Clear();
        }
        EmitStatus();
        return count;
    }

    public bool Remove(DownloadRequest req)
    {
        bool found;
        lock (_lock) found = _queue.Remove(req);
        if (found) EmitStatus();
        return found;
    }

    public void Pause() { lock (_lock) _paused = true; EmitStatus(); }
    public void Resume() { lock (_lock) _paused = false; _newItems.Set(); EmitStatus(); }

    public QueueStatus Status()
    {
        lock (_lock)
        {
            return new QueueStatus(_running, _queue.ToArray(), _paused, _completed);
        }
    }

    private async Task ResolveTitleAsync(DownloadRequest req)
    {
        CancellationToken ct = _probeCts.Token;
        try
        {
            await _probeSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DownloadInfo info = await _backend
                    .ProbeAsync(req, _config, ct)
                    .ConfigureAwait(false);
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(req.Kind) && info.Kind is not null)
                        req.Kind = info.Kind;
                    if (string.IsNullOrEmpty(req.Title) && info.Title is not null)
                        req.Title = info.Title;
                    if (string.IsNullOrEmpty(req.VideoId) && info.VideoId is not null)
                        req.VideoId = info.VideoId;
                    if (req.Mode == "audio"
                        && string.IsNullOrEmpty(req.ResolvedTitle)
                        && info.ResolvedTitle is not null)
                    {
                        req.ResolvedTitle = info.ResolvedTitle;
                    }
                    req.TitleProbed = true;
                }
                EmitStatus();
            }
            finally
            {
                _probeSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 停止中のプローブは無視する。
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"タイトルの取得に失敗しました: {req.Url}: {ex.Message}");
            lock (_lock) req.TitleProbed = true;
            EmitStatus();
        }
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var item = PopNext();
            if (item is null)
            {
                _newItems.WaitOne(TimeSpan.FromMilliseconds(200));
                EmitStatus();
                continue;
            }

            try
            {
                await RunAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                item.Error = "キャンセルされました";
                IssueRecorder?.AddFailed(item.Url, item.Title);
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                AppLogger.Exception($"キューの処理に失敗しました: {item.Url}", ex);
                IssueRecorder?.AddFailed(item.Url, item.Title);
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_running, item)) _running = null;
                    item.Done = true;
                    _completed++;
                }
                EmitStatus();
            }
        }
    }

    private async Task RunAsync(DownloadRequest item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.Url))
        {
            item.Url = Environment.GetEnvironmentVariable("YTB_DOWNLOADER_URL")
                ?? item.Url;
        }

        await _backend.DownloadAsync(
            item,
            _config,
            info =>
            {
                lock (_lock)
                {
                    item.Kind = info.Kind ?? item.Kind;
                    item.Title = info.Title ?? item.Title;
                    item.VideoId = info.VideoId ?? item.VideoId;
                    if (item.Mode == "audio" && !string.IsNullOrEmpty(info.ResolvedTitle))
                        item.ResolvedTitle = info.ResolvedTitle;
                }
                EmitStatus();
            },
            progress => EmitProgress(item, progress),
            ct).ConfigureAwait(false);
    }

    private void EmitProgress(DownloadRequest item, DownloadProgress progress)
    {
        lock (_lock) item.Progress = progress;

        DateTime now = DateTime.UtcNow;
        if (progress.Percent < 100
            && now - _lastProgressEmitUtc < TimeSpan.FromMilliseconds(200))
        {
            return;
        }
        _lastProgressEmitUtc = now;
        EmitStatus();
    }

    private void EmitStatus()
    {
        var status = Status();
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"キューステータスの通知に失敗しました: {ex.Message}");
        }
    }
}
