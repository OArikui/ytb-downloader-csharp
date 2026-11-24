using System.Windows;
using System.Windows.Threading;

namespace YtbDownloader.Tray;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly double _anchorHeight;

    public ToastWindow(
        string message,
        double anchorHeight = 0,
        int dismissAfterMs = 6000)
    {
        InitializeComponent();
        _anchorHeight = anchorHeight;
        MessageText.Text = message;
        Loaded += (_, _) => PositionToast();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(dismissAfterMs) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    public void Update(string message)
    {
        if (IsVisible) MessageText.Text = message;
    }

    private void PositionToast()
    {
        var work = SystemParameters.WorkArea;
        UpdateLayout();
        Left = work.Right - ActualWidth - 8;
        Top = work.Bottom - _anchorHeight - 8 - ActualHeight - 8;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
