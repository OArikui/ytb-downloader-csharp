using YtbDownloader.Config;
using YtbDownloader.Core;
using YtbDownloader.Logging;

namespace YtbDownloader.Queue;

public sealed class DownloadQueue
{
    private readonly AppConfig _config;
    private readonly IDownloadBackend _backend;
    private readonly object _lock = new();
    private readonly List<DownloadRequest> _queue = [];
    private readonly AutoResetEvent _newItems = new(false);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _paused;
    private DownloadRequest? _running;

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
        _newItems.Set();
        bool stopped = worker?.Wait(timeout ?? TimeSpan.FromSeconds(3)) ?? true;
        if (stopped)
            cts?.Dispose();
    }

    public DownloadRequest Enqueue(string url, string mode, int priority = 0)
    {
        var req = new DownloadRequest
        {
            Url = url,
            Mode = mode,
            Priority = priority,
            Description = url,
        };
        lock (_lock)
        {
            if (priority > 0) _queue.Insert(0, req);
            else _queue.Add(req);
        }
        EmitStatus();
        _newItems.Set();
        return req;
    }

    public DownloadRequest? PopNext()
    {
        lock (_lock)
        {
            if (_paused || _queue.Count == 0) return null;
            var item = _queue[0];
            _queue.RemoveAt(0);
            _running = item;
            return item;
        }
    }

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
            return new QueueStatus(_running, _queue.ToArray(), _paused);
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
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                AppLogger.Exception($"キューの処理に失敗しました: {item.Url}", ex);
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_running, item)) _running = null;
                    item.Done = true;
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

        await _backend.DownloadAsync(item, _config, info =>
        {
            lock (_lock)
            {
                item.Kind = info.Kind;
                item.Title = info.Title;
                item.VideoId = info.VideoId;
            }
            EmitStatus();
        }, ct).ConfigureAwait(false);
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
