using System.Collections.Concurrent;

namespace PkgRelay.Services;

public sealed class PackageLibraryWatcherService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly InstallQueueService _queue;
    private readonly ConcurrentDictionary<string, byte> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private string _activeFolder = "";

    public PackageLibraryWatcherService(
        SettingsService settings,
        LogService log,
        InstallQueueService queue)
    {
        _settings = settings;
        _log = log;
        _queue = queue;
    }

    public void ApplySettings()
    {
        var settings = _settings.Current;
        var folder = settings.PackageFolder?.Trim() ?? "";

        if (!settings.WatchDefaultPkgFolder ||
            string.IsNullOrWhiteSpace(folder) ||
            !Directory.Exists(folder))
        {
            Stop();
            return;
        }

        folder = Path.GetFullPath(folder);

        if (_watcher is not null &&
            string.Equals(
                _activeFolder,
                folder,
                StringComparison.OrdinalIgnoreCase) &&
            _watcher.IncludeSubdirectories == settings.RecursiveScan)
        {
            return;
        }

        Stop();

        _activeFolder = folder;

        _watcher = new FileSystemWatcher(folder, "*.pkg")
        {
            IncludeSubdirectories = settings.RecursiveScan,
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.Size |
                NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _watcher.Created += Watcher_FileAppeared;
        _watcher.Renamed += Watcher_FileAppeared;
        _watcher.Error += Watcher_Error;

        _ = _log.InfoAsync(
            $"Watching PKG library folder: {folder} " +
            $"(recursive={settings.RecursiveScan}).");
    }

    private void Watcher_FileAppeared(
        object sender,
        FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(
                ".pkg",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_pending.TryAdd(e.FullPath, 0))
            return;

        _ = AddWhenReadyAsync(e.FullPath);
    }

    private async Task AddWhenReadyAsync(string path)
    {
        try
        {
            long previousLength = -1;
            var stableSamples = 0;

            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(500);

                if (!File.Exists(path))
                    continue;

                long length;

                try
                {
                    length = new FileInfo(path).Length;
                }
                catch
                {
                    continue;
                }

                if (length > 0 && length == previousLength)
                    stableSamples++;
                else
                    stableSamples = 0;

                previousLength = length;

                if (stableSamples >= 2)
                    break;
            }

            if (!File.Exists(path))
                return;

            await _queue.AddLocalAsync(path);

            await _log.InfoAsync(
                $"PKG library watcher added: {path}");
        }
        catch (Exception ex)
        {
            await _log.WarningAsync(
                $"PKG library watcher could not add '{path}': {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(path, out _);
        }
    }

    private void Watcher_Error(
        object sender,
        ErrorEventArgs e)
    {
        _ = _log.WarningAsync(
            $"PKG library watcher error: {e.GetException().Message}");
    }

    private void Stop()
    {
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= Watcher_FileAppeared;
        _watcher.Renamed -= Watcher_FileAppeared;
        _watcher.Error -= Watcher_Error;
        _watcher.Dispose();
        _watcher = null;
        _activeFolder = "";
    }

    public void Dispose()
    {
        Stop();
    }
}
