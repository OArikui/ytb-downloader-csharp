using System.Windows;
using YtbDownloader.Config;
using YtbDownloader.Queue;
using YtbDownloader.Tray;

namespace YtbDownloader;

public partial class App : System.Windows.Application
{
    public AppConfig? BootstrapConfig { get; init; }
    public DownloadQueue? BootstrapQueue { get; init; }

    private TrayApp? _trayApp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (BootstrapConfig is not null && BootstrapQueue is not null)
        {
            _trayApp = new TrayApp(BootstrapConfig, BootstrapQueue, Dispatcher);
            _trayApp.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayApp?.Dispose();
        base.OnExit(e);
    }
}
