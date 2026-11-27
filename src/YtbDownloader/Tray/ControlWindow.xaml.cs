using System.Windows;
using System.Windows.Input;
using YtbDownloader.Queue;

namespace YtbDownloader.Tray;

public partial class ControlWindow : Window
{
    private readonly TrayApp _app;
    private bool _allowClose;
    private string? _lastQueueSignature;

    public ControlWindow(TrayApp app)
    {
        _app = app;
        InitializeComponent();
        Loaded += (_, _) => PositionNearBottomRight();
    }

    public void RefreshStatus(QueueStatus status)
    {
        int total = status.CompletedCount
            + status.Queued.Count
            + (status.Running is not null ? 1 : 0);
        string progress = status.Running is not null
            ? $"実行中 ({status.CompletedCount}/{total}完了, 待ち {status.Queued.Count}): " +
              TrayApp.DescribeRunning(status.Running)
            : $"待機中 (待ち {status.Queued.Count}件, 完了 {status.CompletedCount}件)";
        StatusText.Text = status.Paused ? progress + " [一時停止]" : progress;

        var items = status.Queued.Count == 0
            ? new[] { "(キューなし)" }
            : status.Queued
                .Select((item, index) => $"{index + 1}. {DownloadRequest.Describe(item)}")
                .ToArray();

        // 進捗更新ではキュー内容は変わらないため、再バインドしてスクロール位置を
        // リセットしないようにする。
        string signature = string.Join("\n", items);
        if (signature != _lastQueueSignature)
        {
            _lastQueueSignature = signature;
            QueueList.ItemsSource = items;
        }

        PositionNearBottomRight();
    }

    private void PositionNearBottomRight()
    {
        var work = SystemParameters.WorkArea;
        UpdateLayout();
        Left = work.Right - ActualWidth - 8;
        Top = work.Bottom - ActualHeight - 8;
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void StackButton_Click(object sender, RoutedEventArgs e) => _app.EnqueueFromInput(InputBox.Text, 0);
    private void RunNowButton_Click(object sender, RoutedEventArgs e) => _app.EnqueueFromInput(InputBox.Text, 1);
    private void ClearButton_Click(object sender, RoutedEventArgs e) => _app.ClearQueue();
    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        _app.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _app.EnqueueFromInput(InputBox.Text, 0);
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // Hide instead of close, like Python behavior.
        e.Cancel = true;
        Hide();
    }
}
