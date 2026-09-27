using Microsoft.UI.Dispatching;
using PkgRelay.Infrastructure;
using PkgRelay.Services;

namespace PkgRelay;

public sealed class AppServices
{
    public SettingsService Settings { get; }
    public LogService Log { get; }
    public NetworkService Network { get; }
    public PkgHttpServer Server { get; }
    public PkgMetadataService Metadata { get; }
    public QueueStoreService QueueStore { get; }
    public Ps4RpiClient Rpi { get; }
    public DownloadService Downloads { get; }
    public HomebrewService Homebrew { get; }
    public NetworkDiagnosticsService Diagnostics { get; }
    public InstallQueueService Queue { get; }
    public PackageLibraryWatcherService LibraryWatcher { get; }

    public AppServices()
    {
        Settings = new SettingsService();
        Log = new LogService(Settings);
        Network = new NetworkService();
        Server = new PkgHttpServer(Log);
        Metadata = new PkgMetadataService();
        QueueStore = new QueueStoreService(Settings);
        Rpi = new Ps4RpiClient(Settings, Log);
        Downloads = new DownloadService(Settings, Log);
        Homebrew = new HomebrewService(Settings, Log);
        Diagnostics = new NetworkDiagnosticsService(
            Settings,
            Network,
            Server,
            Rpi);

        Queue = new InstallQueueService(
            Settings,
            Log,
            Network,
            Server,
            Metadata,
            QueueStore,
            Rpi);

        LibraryWatcher = new PackageLibraryWatcherService(
            Settings,
            Log,
            Queue);
    }

    public void AttachDispatcher(DispatcherQueue dispatcher)
    {
        Queue.AttachDispatcher(new UiDispatcher(dispatcher));
    }

    public async Task ShutdownAsync()
    {
        LibraryWatcher.Dispose();
        await Queue.DisposeAsync();
        Diagnostics.Dispose();
        Rpi.Dispose();
        Homebrew.Dispose();
        Downloads.Dispose();
    }
}
