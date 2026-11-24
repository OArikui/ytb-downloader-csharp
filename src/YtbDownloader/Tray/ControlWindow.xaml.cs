using System.Windows;
using System.Windows.Input;
using YtbDownloader.Queue;

namespace YtbDownloader.Tray;

public partial class ControlWindow : Window
{
    private readonly TrayApp _app;
    private bool _allowClose;

    public ControlWindow(TrayApp app)
    {
        _app = app;
        InitializeComponent();
        Loaded += (_, _) => PositionNearBottomRight();
    }

    public void RefreshStatus(QueueStatus status)
    {
        StatusText.Text = status.Running is not null
            ? $"実行中: {DownloadRequest.Describe(status.Running)}"
            : status.Queued.Count > 0 ? $"待機中({status.Queued.Count}件)" : "待機中";

        if (status.Queued.Count == 0)
        {
            QueueText.Text = "(キューなし)";
        }
        else
        {
            var lines = status.Queued
                .Select(x => $"- {DownloadRequest.Describe(x)}")
                .ToList();
            if (lines.Count > 8)
                lines = [$"... 他{lines.Count - 8}件", .. lines.TakeLast(8)];
            QueueText.Text = string.Join(Environment.NewLine, lines);
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
