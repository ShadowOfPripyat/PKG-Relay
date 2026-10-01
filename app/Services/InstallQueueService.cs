using System.Collections.ObjectModel;
using System.Net.Sockets;
using PkgRelay.Infrastructure;
using PkgRelay.Models;

namespace PkgRelay.Services;

public sealed class InstallQueueService : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly NetworkService _network;
    private readonly PkgHttpServer _server;
    private readonly PkgMetadataService _metadata;
    private readonly QueueStoreService _store;
    private readonly Ps4RpiClient _rpi;
    private readonly InstalledPackageVerifier _installed;

    private readonly PkgQueueScheduler _scheduler;
    private readonly CancellationTokenSource _lifetime = new();

    private UiDispatcher? _ui;
    private readonly Task _reconciliation;

    public ObservableCollection<InstallJob> Jobs { get; } = new();

    public event EventHandler<QueueErrorEventArgs>? ErrorOccurred;
    public event EventHandler<QueueErrorEventArgs>? WarningOccurred;

    public bool IsRunning => _scheduler.IsRunning;

    public InstallQueueService(
        SettingsService settings,
        LogService log,
        NetworkService network,
        PkgHttpServer server,
        PkgMetadataService metadata,
        QueueStoreService store,
        Ps4RpiClient rpi)
    {
        _settings = settings;
        _log = log;
        _network = network;
        _server = server;
        _metadata = metadata;
        _store = store;
        _rpi = rpi;
        _installed = new InstalledPackageVerifier(settings);
        _scheduler = new PkgQueueScheduler(GetJobsSnapshotAsync,
            () => _settings.Current.ConsoleQueueLimit, StartJobWorkerAsync);
        _reconciliation = ReconcileFailedJobsAsync();
    }

    public void AttachDispatcher(UiDispatcher dispatcher)
    {
        _ui = dispatcher;
    }

    public async Task RestoreAsync()
    {
        var jobs = await _store.LoadAsync();

        foreach (var job in jobs)
            await AddOnUiAsync(job);

        await ReindexAsync();

        foreach (var job in jobs.Where(NeedsPackageSource)
                     .OrderBy(x => string.IsNullOrWhiteSpace(x.PackageUri)))
        {
            try
            {
                await RestorePackageRouteAsync(job, _lifetime.Token);
            }
            catch (Exception ex) when (!_lifetime.IsCancellationRequested)
            {
                await _log.WarningAsync($"Package source restore failed for {job.DisplayName}: {ex.Message}");
                await ReportWarningAsync("Package source unavailable", ex.Message, job);
            }
        }

        if (Jobs.Any(x =>
                x.Status is
                    InstallStatus.Recovering or
                    InstallStatus.Paused))
        {
            _ = RecoverActiveJobsSafelyAsync();
        }
    }

    public async Task<InstallJob> AddLocalAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);

        var jobsSnapshot =
            await GetJobsSnapshotAsync();

        var existing = jobsSnapshot.FirstOrDefault(
            x => x.SourceKind == InstallSourceKind.LocalFile &&
                 string.Equals(
                     x.FilePath,
                     fullPath,
                     StringComparison.OrdinalIgnoreCase) &&
                 x.Status is not InstallStatus.Completed
                     and not InstallStatus.Cancelled);

        if (existing is not null)
        {
            TriggerAutoStart();
            return existing;
        }

        var metadata =
            await _metadata.ReadAsync(fullPath, cancellationToken);

        if (!metadata.IsValid)
            throw new InvalidOperationException(
                "This file does not have a valid PS4 PKG header.");

        if (!string.IsNullOrWhiteSpace(metadata.ContentId))
        {
            var duplicate =
                jobsSnapshot.FirstOrDefault(x =>
                    x.Kind == metadata.Kind &&
                    string.Equals(
                        x.ContentId,
                        metadata.ContentId,
                        StringComparison.OrdinalIgnoreCase) &&
                    x.Status is not
                        InstallStatus.Completed and not
                        InstallStatus.Cancelled);

            if (duplicate is not null)
            {
                await ReportWarningAsync(
                    "Duplicate package skipped",
                    $"{Path.GetFileName(fullPath)} has the same content ID and package type as " +
                    $"{duplicate.DisplayName}. The existing queue item was kept.",
                    duplicate);

                TriggerAutoStart();
                return duplicate;
            }
        }

        var job = new InstallJob
        {
            SourceKind = InstallSourceKind.LocalFile,
            FilePath = fullPath,
            DisplayName = metadata.Title,
            TitleId = metadata.TitleId,
            ContentId = metadata.ContentId,
            Kind = metadata.Kind,
            FileSize = metadata.FileSize,
            Category = metadata.Category,
            AppVersion = metadata.AppVersion,
            Version = metadata.Version,
            RequiredFirmware = metadata.RequiredFirmware,
            Status = InstallStatus.Waiting
        };

        await AddOnUiAsync(job);
        await ReindexAsync();
        await PersistAsync();

        await WarnAboutMissingBaseAsync(job);

        TriggerAutoStart();
        return job;
    }

    public async Task<InstallJob> AddRemoteAsync(
        string packageUrl,
        string displayName,
        string titleId = "",
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Homebrew package URL is invalid.");
        }

        var jobsSnapshot =
            await GetJobsSnapshotAsync();

        var existing = jobsSnapshot.FirstOrDefault(
            x => x.SourceKind == InstallSourceKind.RemoteUrl &&
                 string.Equals(
                     x.RemoteUrl,
                     uri.AbsoluteUri,
                     StringComparison.OrdinalIgnoreCase) &&
                 x.Status is not InstallStatus.Completed
                     and not InstallStatus.Cancelled);

        if (existing is not null)
        {
            TriggerAutoStart();
            return existing;
        }

        var job = new InstallJob
        {
            SourceKind = InstallSourceKind.RemoteUrl,
            RemoteUrl = uri.AbsoluteUri,
            DisplayName =
                string.IsNullOrWhiteSpace(displayName)
                    ? Path.GetFileName(uri.LocalPath)
                    : displayName,
            TitleId = titleId,
            Kind = PkgKind.Homebrew,
            Status = InstallStatus.Waiting
        };

        await AddOnUiAsync(job);
        await ReindexAsync();
        await PersistAsync();

        TriggerAutoStart();
        return job;
    }

    public void Start() => _ = StartPendingSafelyAsync();

    public async Task StartPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _scheduler.RunAsync(includeWaiting: true, _lifetime.Token).WaitAsync(cancellationToken);
    }

    public void NotifySettingsChanged() => _scheduler.Pulse();

    private async Task<Task> StartJobWorkerAsync(InstallJob job, CancellationToken token)
    {
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = RunJobWorkerAsync(job, token, registered);
        await registered.Task;
        return worker;
    }

    private async Task RunJobWorkerAsync(InstallJob job, CancellationToken token, TaskCompletionSource registered)
    {
        var recovering = job.Status is InstallStatus.Recovering or InstallStatus.Paused;
        try
        {
            if (recovering) await RecoverJobAsync(job, token, registered);
            else await ProcessJobAsync(job, token, registered);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            await UpdateAndPersistAsync(job, x =>
            {
                x.Status = InstallStatus.Cancelled;
                x.Message = "Cancelled";
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _log.ErrorAsync($"Queue job failed for {job.DisplayName}.", ex);
            await UpdateAndPersistAsync(job, x =>
            {
                x.Status = InstallStatus.Failed;
                x.Message = recovering ? "Recovery failed" : "Failed";
                x.TransferRateBytesPerSecond = 0;
                x.EtaSeconds = null;
            });
            await ReportErrorAsync($"Install failed: {job.DisplayName}", ex.Message, job);
        }
        finally { registered.TrySetResult(); }
    }
    public async Task PauseAsync(InstallJob job)
    {
        if (!job.TaskId.HasValue ||
            job.Status != InstallStatus.Installing)
            return;

        try { await _rpi.PauseAsync(job.TaskId.Value); }
        catch (RpiException) { if (await CheckInstalledAsync(job)) return; throw; }
        if (job.Status == InstallStatus.Completed) return;

        await UpdateAndPersistAsync(job, x =>
        {
            x.Status = InstallStatus.Paused;
            x.Message = "Paused";
            x.TransferRateBytesPerSecond = 0;
            x.EtaSeconds = null;
        });
    }

    public async Task ResumeAsync(InstallJob job)
    {
        if (!job.TaskId.HasValue ||
            job.Status != InstallStatus.Paused)
            return;

        try { await _rpi.ResumeAsync(job.TaskId.Value); }
        catch (RpiException) { if (await CheckInstalledAsync(job)) return; throw; }
        if (job.Status == InstallStatus.Completed) return;

        await UpdateAndPersistAsync(job, x =>
        {
            x.Status = InstallStatus.Installing;
            x.Message = "Installing";
        });
    }

    public async Task CancelAsync(InstallJob job)
    {
        job.Cancellation.Cancel();

        if (job.TaskId.HasValue)
        {
            try
            {
                await _rpi.CancelAsync(job.TaskId.Value);
            }
            catch (Exception ex)
            {
                await _log.WarningAsync(
                    $"RPI cancel failed for task {job.TaskId.Value}: {ex.Message}");
            }
        }

        await UpdateAsync(job, x =>
        {
            x.Status = InstallStatus.Cancelled;
            x.Message = "Cancelled";
            x.TransferRateBytesPerSecond = 0;
            x.EtaSeconds = null;
        });

        await PersistAsync();
        await ReleasePackageRouteIfUnusedAsync(GetSavedRouteId(job));
    }

    public async Task RemoveAsync(InstallJob job)
    {
        if (job.Status is InstallStatus.Installing
            or InstallStatus.Paused
            or InstallStatus.Preparing
            or InstallStatus.Sending
            or InstallStatus.Recovering
            or InstallStatus.Finalizing)
        {
            throw new InvalidOperationException(
                "Cancel an active job before removing it.");
        }

        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() => Jobs.Remove(job));
        await ReindexAsync();
        await PersistAsync();
        await ReleasePackageRouteIfUnusedAsync(GetSavedRouteId(job));
    }

    public async Task ClearFinishedAsync()
    {
        var jobs = await GetJobsSnapshotAsync();
        await RemoveManyAsync(jobs.Where(x => x.Status is
            InstallStatus.Completed or InstallStatus.Cancelled or InstallStatus.Failed));
    }

    public async Task MoveUpAsync(InstallJob job)
    {
        if (!job.CanMove)
            return;

        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() =>
        {
            var index = Jobs.IndexOf(job);
            if (index > 0 && Jobs[index - 1].Status == InstallStatus.Waiting)
                Jobs.Move(index, index - 1);
        });

        await ReindexAsync();
        await PersistAsync();
    }

    public async Task MoveDownAsync(InstallJob job)
    {
        if (!job.CanMove)
            return;

        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() =>
        {
            var index = Jobs.IndexOf(job);
            if (index >= 0 && index < Jobs.Count - 1 && Jobs[index + 1].Status == InstallStatus.Waiting)
                Jobs.Move(index, index + 1);
        });

        await ReindexAsync();
        await PersistAsync();
    }

    public async Task MoveTopAsync(InstallJob job)
    {
        if (!job.CanMove)
            return;

        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() =>
        {
            var index = Jobs.IndexOf(job);
            if (index < 0)
                return;

            var target = 0;

            while (target < Jobs.Count &&
                   Jobs[target].Status != InstallStatus.Waiting)
            {
                target++;
            }

            if (target < index)
                Jobs.Move(index, target);
        });

        await ReindexAsync();
        await PersistAsync();
    }

    public async Task MoveBottomAsync(InstallJob job)
    {
        if (!job.CanMove)
            return;

        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() =>
        {
            var index = Jobs.IndexOf(job);
            if (index < 0)
                return;

            var target = Jobs.Count - 1;

            while (target >= 0 &&
                   Jobs[target].Status != InstallStatus.Waiting)
            {
                target--;
            }

            if (target > index)
                Jobs.Move(index, target);
        });

        await ReindexAsync();
        await PersistAsync();
    }

    public async Task RetryAsync(InstallJob job)
    {
        if (!job.CanRetry)
            return;

        await UpdateAsync(job, x =>
        {
            x.ResetForRetry();
        });

        await PersistAsync();
        TriggerAutoStart();
    }

    public async Task<int> RetryAllFailedAsync()
    {
        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        var count = 0;

        await _ui.RunAsync(() =>
        {
            foreach (var job in Jobs)
            {
                if (job.Status != InstallStatus.Failed)
                    continue;

                job.ResetForRetry();
                count++;
            }
        });

        if (count > 0)
        {
            await PersistAsync();
            TriggerAutoStart();
        }

        return count;
    }

    public async Task<int> RetryManyAsync(
        IEnumerable<InstallJob> jobs)
    {
        var targets =
            jobs.Distinct().Where(x => x.CanRetry).ToArray();

        foreach (var job in targets)
            await RetryAsync(job);

        return targets.Length;
    }

    public async Task<int> CancelManyAsync(
        IEnumerable<InstallJob> jobs)
    {
        var targets =
            jobs.Distinct().Where(x => x.CanCancel).ToArray();

        foreach (var job in targets)
            await CancelAsync(job);

        return targets.Length;
    }

    public async Task<int> RemoveManyAsync(
        IEnumerable<InstallJob> jobs)
    {
        var targets =
            jobs.Distinct()
                .Where(x => x.Status is not
                    InstallStatus.Installing and not
                    InstallStatus.Paused and not
                    InstallStatus.Preparing and not
                    InstallStatus.Sending and not
                    InstallStatus.Recovering and not
                    InstallStatus.Finalizing)
                .ToArray();

        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() =>
        {
            foreach (var job in targets)
                Jobs.Remove(job);
        });

        await ReindexAsync();
        await PersistAsync();

        foreach (var job in targets)
            await ReleasePackageRouteIfUnusedAsync(GetSavedRouteId(job));
        return targets.Length;
    }

    public async Task PersistOrderAsync()
    {
        await ReindexAsync();
        await PersistAsync();
    }

    private async Task ProcessJobAsync(
        InstallJob job,
        CancellationToken applicationToken,
        TaskCompletionSource registered)
    {
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                applicationToken,
                job.Cancellation.Token);

        var cancellationToken = linked.Token;

        await UpdateAsync(job, x =>
        {
            x.Status = InstallStatus.Preparing;
            x.Message = "Preparing";
            x.Progress = 0;
        });

        // Resolve the PS4 first. If automatic rediscovery changes its IP,
        // the local HTTP server can then select the correct route/interface.
        await EnsurePs4ConnectionAsync(
            job,
            cancellationToken);

        Uri packageUri;
        string? routeId = null;

        try
        {
            if (job.SourceKind == InstallSourceKind.LocalFile)
            {
                await EnsureServerAsync(cancellationToken);
                routeId = _server.Register(job.FilePath);
                packageUri = _server.GetPackageUri(routeId);
            }
            else
            {
                packageUri = new Uri(job.RemoteUrl);
            }

            await UpdateAndPersistAsync(job, x =>
            {
                x.PackageUri = packageUri.AbsoluteUri;
            });

            await UpdateAndPersistAsync(job, x =>
            {
                x.Status = InstallStatus.Sending;
                x.Message = "Sending install request";
            });

            var taskId =
                await _rpi.InstallDirectAsync(
                    [packageUri],
                    cancellationToken,
                    job.ContentId,
                    GetTaskSubType(job.Kind));

            await UpdateAndPersistAsync(job, x =>
            {
                x.TaskId = taskId;
                x.Status = InstallStatus.Installing;
                x.Message = "Installing";
            });

            registered.TrySetResult();
            await MonitorAsync(job, cancellationToken);
        }
        finally
        {
            if (routeId is not null)
                await ReleasePackageRouteIfUnusedAsync(routeId);
        }
    }

    private async Task WarnAboutMissingBaseAsync(
        InstallJob job)
    {
        if (job.Kind is not PkgKind.Update and not PkgKind.Dlc ||
            string.IsNullOrWhiteSpace(job.TitleId))
        {
            return;
        }

        var jobs =
            await GetJobsSnapshotAsync();

        var hasBase =
            jobs.Any(x =>
                !ReferenceEquals(x, job) &&
                x.Kind == PkgKind.Base &&
                string.Equals(
                    x.TitleId,
                    job.TitleId,
                    StringComparison.OrdinalIgnoreCase));

        if (hasBase)
            return;

        await ReportWarningAsync(
            job.Kind == PkgKind.Update
                ? "Update has no matching base in the queue"
                : "DLC has no matching base in the queue",
            $"{job.DisplayName} ({job.TitleId}) was added, but no Base package for the same title is currently queued. " +
            "This is only a warning; the base game may already be installed on the PS4.",
            job);
    }

    private async Task RecoverActiveJobsSafelyAsync()
    {
        try
        {
            await _scheduler.RunAsync(_settings.Current.AutoStartPackages, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { await _log.ErrorAsync("Queue recovery failed.", ex); }
    }
    private async Task RecoverJobAsync(
        InstallJob job,
        CancellationToken applicationToken,
        TaskCompletionSource registered)
    {
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                applicationToken,
                job.Cancellation.Token);

        var cancellationToken =
            linked.Token;

        string? routeId = null;

        try
        {
            if (job.SourceKind == InstallSourceKind.LocalFile)
            {
                routeId =
                    await RestorePackageRouteAsync(
                        job,
                        cancellationToken);
            }

            if (!job.TaskId.HasValue)
            {
                if (string.IsNullOrWhiteSpace(job.ContentId))
                {
                    throw new RpiException(
                        "The interrupted task has no saved task ID or content ID, so it cannot be recovered safely.");
                }

                await UpdateAndPersistAsync(job, x =>
                {
                    x.Status = InstallStatus.Recovering;
                    x.Message = "Finding PS4 task by content ID";
                });

                var taskId =
                    await _rpi.FindTaskAsync(
                        job.ContentId,
                        GetTaskSubType(job.Kind),
                        cancellationToken);

                await UpdateAndPersistAsync(job, x =>
                {
                    x.TaskId = taskId;
                    x.Message = $"Recovered PS4 task {taskId}";
                });
            }

            await _log.InfoAsync(
                $"Recovering PS4 task {job.TaskId} for {job.DisplayName}.");

            if (job.Status != InstallStatus.Paused)
            {
                await UpdateAndPersistAsync(job, x =>
                {
                    x.Status = InstallStatus.Recovering;
                    x.Message = $"Monitoring restored task {x.TaskId}";
                });
            }

            registered.TrySetResult();
            await MonitorAsync(
                job,
                cancellationToken);
        }
        finally
        {
            if (routeId is not null)
                await ReleasePackageRouteIfUnusedAsync(routeId);
        }
    }

    private static bool NeedsPackageSource(InstallJob job) =>
        job.SourceKind == InstallSourceKind.LocalFile &&
        job.Status is not InstallStatus.Completed and not InstallStatus.Cancelled &&
        (!string.IsNullOrWhiteSpace(job.PackageUri) || job.Status == InstallStatus.Waiting);

    private static string? GetSavedRouteId(InstallJob job) =>
        job.SourceKind != InstallSourceKind.LocalFile ? null :
        Uri.TryCreate(job.PackageUri, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath.StartsWith("/pkg/", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath[5..] :
        File.Exists(job.FilePath) ? PkgHttpServer.GetRouteId(Path.GetFullPath(job.FilePath)) : null;

    private async Task ReleasePackageRouteIfUnusedAsync(string? routeId)
    {
        if (routeId is null)
            return;
        var jobs = await GetJobsSnapshotAsync();
        if (!jobs.Any(x => NeedsPackageSource(x) &&
                           string.Equals(GetSavedRouteId(x), routeId, StringComparison.OrdinalIgnoreCase)))
            _server.Unregister(routeId);
    }

    private async Task<string> RestorePackageRouteAsync(
        InstallJob job,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.PackageUri) && job.Status == InstallStatus.Waiting)
        {
            // Serve older queues whose Retry reset discarded the previous URL.
            await EnsureServerAsync(cancellationToken);
            return _server.Register(job.FilePath);
        }
        if (string.IsNullOrWhiteSpace(job.PackageUri) ||
            !Uri.TryCreate(
                job.PackageUri,
                UriKind.Absolute,
                out var savedUri) ||
            savedUri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(
                savedUri.Host,
                out var savedAddress))
        {
            throw new InvalidOperationException(
                "The interrupted local install does not contain a recoverable package-server URL.");
        }

        var routeId = PkgHttpServer.GetRouteId(Path.GetFullPath(job.FilePath));
        if (!string.Equals(savedUri.AbsolutePath, $"/pkg/{routeId}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved package URL no longer matches the deterministic package route.");

        if (_server.IsRunning)
        {
            if (_server.Address is null ||
                !_server.Address.Equals(savedAddress) ||
                _server.Port != savedUri.Port)
            {
                throw new InvalidOperationException(
                    $"The package server is already running on {_server.Address}:{_server.Port}, " +
                    $"but the interrupted PS4 task requires {savedAddress}:{savedUri.Port}.");
            }
        }
        else
        {
            await _server.StartAsync(
                savedAddress,
                savedUri.Port,
                cancellationToken);
        }

        var restoredUri =
            _server.GetPackageUri(routeId);

        if (Uri.Compare(
                restoredUri,
                savedUri,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException(
                $"Unable to restore the exact package URL expected by the PS4. " +
                $"Expected {savedUri}, restored {restoredUri}.");
        }

        _server.Register(job.FilePath);

        await _log.InfoAsync(
            $"Restored package route for interrupted task: {restoredUri}");

        return routeId;
    }

    private static int GetTaskSubType(
        PkgKind kind)
    {
        return kind switch
        {
            PkgKind.Update => 8,
            PkgKind.Dlc => 7,
            _ => 6
        };
    }

    private void TriggerAutoStart()
    {
        if (!_settings.Current.AutoStartPackages)
            return;

        _ = StartPendingSafelyAsync();
    }

    private async Task StartPendingSafelyAsync()
    {
        try
        {
            await StartPendingAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _log.ErrorAsync(
                "Automatic queue start failed.",
                ex);

            await ReportErrorAsync(
                "Automatic queue start failed",
                ex.Message);
        }
    }

    private Task ReportErrorAsync(
        string title,
        string message,
        InstallJob? job = null)
    {
        if (_ui is null)
            return Task.CompletedTask;

        return _ui.RunAsync(() =>
        {
            ErrorOccurred?.Invoke(
                this,
                new QueueErrorEventArgs(
                    title,
                    message,
                    job));
        });
    }

    private Task ReportWarningAsync(
        string title,
        string message,
        InstallJob? job = null)
    {
        if (_ui is null)
            return Task.CompletedTask;

        return _ui.RunAsync(() =>
        {
            WarningOccurred?.Invoke(
                this,
                new QueueErrorEventArgs(
                    title,
                    message,
                    job));
        });
    }

    private async Task EnsurePs4ConnectionAsync(
        InstallJob job,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await UpdateAsync(job, x =>
            {
                x.Message =
                    $"Checking PS4 connection ({attempt}/{maxAttempts})";
            });

            if (await _rpi.TestConnectionAsync(cancellationToken))
            {
                await _log.InfoAsync(
                    $"PS4 connection preflight succeeded on attempt {attempt}/{maxAttempts}.");
                return;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    cancellationToken);
            }
        }

        if (!_settings.Current.AutoDiscoverPs4OnConnectionFailure)
        {
            throw new RpiException(
                "PS4 Remote Package Installer did not respond after 4 connection checks.");
        }

        await RediscoverPs4Async(
            job,
            cancellationToken);
    }

    private async Task RediscoverPs4Async(
        InstallJob job,
        CancellationToken cancellationToken)
    {
        var port = _settings.Current.RpiPort;

        await UpdateAsync(job, x =>
        {
            x.Message = "Searching local network for PS4";
        });

        await _log.WarningAsync(
            $"Configured PS4 at {_settings.Current.Ps4Address}:{port} did not respond. " +
            "Automatic PS4 rediscovery is enabled; scanning local LAN.");

        var hosts =
            await _network.FindRpiHostsAsync(
                port,
                cancellationToken: cancellationToken);

        if (hosts.Count == 0)
        {
            throw new RpiException(
                "PS4 did not respond after 4 connection checks and automatic discovery found no RPI host.");
        }

        if (hosts.Count > 1)
        {
            var preview = string.Join(
                ", ",
                hosts.Take(4).Select(x => x.ToString()));

            var suffix =
                hosts.Count > 4
                    ? ", …"
                    : "";

            throw new RpiException(
                $"PS4 did not respond after 4 connection checks. Automatic discovery found " +
                $"{hosts.Count} RPI hosts ({preview}{suffix}); select the correct PS4 manually in Settings.");
        }

        var discoveredAddress =
            hosts[0].ToString();

        var previousAddress =
            _settings.Current.Ps4Address;

        _settings.Current.Ps4Address =
            discoveredAddress;

        try
        {
            await _settings.SaveAsync();

            await UpdateAsync(job, x =>
            {
                x.Message =
                    $"PS4 found at {discoveredAddress}; verifying connection";
            });

            if (!await _rpi.TestConnectionAsync(cancellationToken))
            {
                throw new RpiException(
                    $"Automatic discovery found RPI at {discoveredAddress}, but the verification connection failed.");
            }

            await _log.InfoAsync(
                $"PS4 automatically rediscovered at {discoveredAddress}. " +
                $"Previous configured address: {previousAddress}.");

            await UpdateAsync(job, x =>
            {
                x.Message =
                    $"PS4 rediscovered at {discoveredAddress}";
            });
        }
        catch
        {
            // Do not leave an unverified address persisted if discovery or
            // verification cannot complete successfully.
            _settings.Current.Ps4Address =
                previousAddress;

            try
            {
                await _settings.SaveAsync();
            }
            catch
            {
                // Preserve the original discovery/verification exception.
            }

            throw;
        }
    }

    private async Task MonitorAsync(
        InstallJob job,
        CancellationToken cancellationToken)
    {
        if (!job.TaskId.HasValue)
            throw new InvalidOperationException(
                "RPI task ID is missing.");

        var failures = 0;
        var warningRaised = false;
        var stallWarningRaised = false;
        var lastTransferred = 0L;
        var lastProgressChange = DateTimeOffset.UtcNow;
        var lastRateSample = DateTimeOffset.UtcNow;
        var smoothedRate = 0d;
        var successfulPollsSincePersist = 0;
        var nextInstalledCheck = DateTimeOffset.UtcNow;

        while (job.Status != InstallStatus.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= nextInstalledCheck)
            {
                nextInstalledCheck = DateTimeOffset.UtcNow.AddSeconds(30);
                if (await CheckInstalledAsync(job)) return;
            }

            var delay =
                GetProgressPollDelay(
                    failures);

            await Task.Delay(
                delay,
                cancellationToken);

            if (job.Status == InstallStatus.Paused)
                continue;

            RpiProgress progress;

            try
            {
                progress =
                    await _rpi.GetProgressAsync(
                        job.TaskId.Value,
                        cancellationToken);
            }
            catch (Exception ex)
                when (!cancellationToken.IsCancellationRequested)
            {
                if (job.Status == InstallStatus.Completed) return;
                failures++;

                var threshold =
                    Math.Max(
                        1,
                        _settings.Current.MaxProgressFailures);

                if (failures == 1 ||
                    failures == threshold ||
                    failures % 10 == 0)
                {
                    await _log.WarningAsync(
                        $"Progress polling failed for task {job.TaskId.Value} " +
                        $"({failures} consecutive failure(s)): {ex.Message}");
                }

                if (failures >= threshold &&
                    !warningRaised)
                {
                    warningRaised = true;

                    await ReportWarningAsync(
                        "PS4 progress monitoring interrupted",
                        "The PS4 already accepted the package, but Remote Package Installer " +
                        "is no longer responding to progress requests. The local PKG URL will " +
                        "remain available and monitoring will keep retrying; the transfer is " +
                        "not cancelled.",
                        job);

                    await PersistAsync();
                }

                await UpdateAsync(job, x =>
                {
                    x.Status = InstallStatus.Installing;
                    x.Message =
                        "Progress monitoring unavailable; keeping PKG server active";
                });

                continue;
            }

            if (failures > 0)
            {
                await _log.InfoAsync(
                    $"Progress monitoring restored for task {job.TaskId.Value} " +
                    $"after {failures} failed request(s).");
            }

            if (job.Status == InstallStatus.Completed) return;
            failures = 0;
            warningRaised = false;

            if (progress.Error != 0)
            {
                throw new RpiException(
                    $"PS4 task error: 0x{progress.Error:X}");
            }

            var now = DateTimeOffset.UtcNow;

            if (progress.TransferredTotal > lastTransferred)
            {
                var elapsed =
                    Math.Max(
                        0.001,
                        (now - lastRateSample).TotalSeconds);

                if (lastTransferred > 0)
                {
                    var instantaneousRate =
                        (progress.TransferredTotal - lastTransferred) /
                        elapsed;

                    smoothedRate =
                        smoothedRate <= 0
                            ? instantaneousRate
                            : (smoothedRate * 0.70) +
                              (instantaneousRate * 0.30);
                }

                lastTransferred =
                    progress.TransferredTotal;

                lastRateSample = now;
                lastProgressChange = now;
                stallWarningRaised = false;
            }

            int? etaSeconds = null;

            if (progress.RestSecondsTotal > 0)
            {
                etaSeconds =
                    progress.RestSecondsTotal;
            }
            else if (
                smoothedRate > 0 &&
                progress.LengthTotal >
                    progress.TransferredTotal)
            {
                etaSeconds =
                    (int)Math.Ceiling(
                        (progress.LengthTotal -
                         progress.TransferredTotal) /
                        smoothedRate);
            }

            await UpdateAsync(job, x =>
            {
                x.Progress =
                    Math.Max(
                        x.Progress,
                        progress.Percent);

                x.Status =
                    InstallStatus.Installing;

                x.TransferRateBytesPerSecond =
                    smoothedRate;

                x.EtaSeconds =
                    etaSeconds;

                if (progress.PreparingPercent
                    is > 0 and < 100)
                {
                    x.Message =
                        $"Preparing {progress.PreparingPercent}%";
                }
                else
                {
                    x.Message = "Installing";
                }
            });

            successfulPollsSincePersist++;

            if (successfulPollsSincePersist >= 6)
            {
                successfulPollsSincePersist = 0;
                await PersistAsync();
            }

            if (progress.LooksComplete)
            {
                await UpdateAndPersistAsync(job, x =>
                {
                    x.Status = InstallStatus.Finalizing;
                    x.Message = "Confirming completion";
                    x.Progress = 100;
                    x.EtaSeconds = null;
                });

                await Task.Delay(
                    Math.Min(
                        2500,
                        Math.Max(
                            1000,
                            _settings.Current.PollIntervalMs)),
                    cancellationToken);

                RpiProgress confirmation;

                try
                {
                    confirmation =
                        await _rpi.GetProgressAsync(
                            job.TaskId.Value,
                            cancellationToken);
                }
                catch (Exception ex)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    // A full transferred_total/length_total sample is already
                    // a strong terminal signal. Some RPI builds stop exposing
                    // the task immediately after BGFT finishes, so a failed
                    // second query must not strand an otherwise completed job.
                    await _log.WarningAsync(
                        $"Completion confirmation was unavailable for task {job.TaskId.Value}: {ex.Message}. " +
                        "Accepting the preceding complete progress sample.");

                    await CompleteJobAsync(job);
                    return;
                }

                if (confirmation.Error != 0)
                {
                    throw new RpiException(
                        $"PS4 task error during completion: 0x{confirmation.Error:X}");
                }

                if (!confirmation.LooksComplete)
                {
                    await UpdateAsync(job, x =>
                    {
                        x.Status = InstallStatus.Installing;
                        x.Message = "Installing";
                        x.Progress =
                            Math.Max(
                                x.Progress,
                                confirmation.Percent);
                    });

                    continue;
                }

                await CompleteJobAsync(job);
                return;
            }

            if (!stallWarningRaised &&
                now - lastProgressChange >
                    TimeSpan.FromMinutes(20))
            {
                stallWarningRaised = true;

                await UpdateAsync(job, x =>
                {
                    x.Message =
                        "No reported transfer movement; still monitoring";
                });

                await ReportWarningAsync(
                    "Install appears stalled",
                    $"{job.DisplayName} has reported no transfer movement for 20 minutes. " +
                    "The package server remains active and monitoring will continue.",
                    job);
            }
        }
    }

    private async Task CompleteJobAsync(InstallJob job)
    {
        await UpdateAndPersistAsync(job, SetCompleted);

        await _log.InfoAsync(
            $"Install completed: {job.DisplayName}");
    }

    private static void SetCompleted(InstallJob job)
    {
        job.Progress = 100;
        job.Status = InstallStatus.Completed;
        job.Message = "Installed";
        job.TransferRateBytesPerSecond = 0;
        job.EtaSeconds = null;
    }

    public async Task<bool> CheckInstalledAsync(InstallJob job)
    {
        var state = job.Status;
        var taskId = job.TaskId;
        if (await _installed.CheckAsync(job, _lifetime.Token) != InstalledPackageState.Installed)
            return false;
        if (_ui is null) return false;
        var confirmed = false;
        await _ui.RunAsync(() =>
        {
            // Serialize with Retry/Cancel and other UI actions after the network wait.
            if (job.TaskId != taskId) return;
            if (job.Status == InstallStatus.Completed) { confirmed = true; return; }
            if (job.Status != state) return;
            SetCompleted(job);
            confirmed = true;
        });
        if (!confirmed) return false;
        await PersistAsync();
        await ReleasePackageRouteIfUnusedAsync(GetSavedRouteId(job));
        return true;
    }

    private async Task ReconcileFailedJobsAsync()
    {
        var offset = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
                if (_ui is null) continue;
                var failed = (await GetJobsSnapshotAsync()).Where(x => x.Status == InstallStatus.Failed &&
                    x.SourceKind == InstallSourceKind.LocalFile && !string.IsNullOrWhiteSpace(x.PackageUri)).ToArray();
                if (failed.Length == 0) continue;
                foreach (var job in failed.Skip(offset % failed.Length).Concat(failed.Take(offset % failed.Length)).Take(4))
                {
                    try { await CheckInstalledAsync(job); }
                    catch (Exception ex) when (!_lifetime.IsCancellationRequested)
                    { await _log.WarningAsync("Install reconciliation unavailable: " + ex.Message); }
                }
                offset = (offset + 4) % failed.Length;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { await _log.WarningAsync("Install reconciliation stopped: " + ex.Message); }
    }

    private int GetProgressPollDelay(
        int consecutiveFailures)
    {
        var normal =
            Math.Max(
                250,
                _settings.Current.EffectivePollIntervalMs);

        if (consecutiveFailures <= 0)
            return normal;

        // Once RPI stops answering, reduce request pressure instead of
        // hammering its HTTP server. The retry delay caps at 30 seconds.
        var shift =
            Math.Min(
                consecutiveFailures,
                4);

        var backedOff =
            normal * (1 << shift);

        return Math.Min(
            30000,
            backedOff);
    }

    private async Task EnsureServerAsync(
        CancellationToken cancellationToken)
    {
        if (_server.IsRunning)
            return;

        var settings = _settings.Current;

        if (string.IsNullOrWhiteSpace(settings.Ps4Address))
            throw new InvalidOperationException(
                "Configure the PS4 address in Settings first.");

        var localAddress =
            _network.ResolveLocalAddress(
                settings.Ps4Address,
                settings.RpiPort,
                settings.LocalAddress);

        await _server.StartAsync(
            localAddress,
            settings.HttpPort,
            cancellationToken);
    }

    private async Task<InstallJob[]> GetJobsSnapshotAsync()
    {
        if (_ui is null)
            return Jobs.ToArray();

        InstallJob[] snapshot = [];

        await _ui.RunAsync(() =>
        {
            snapshot = Jobs.ToArray();
        });

        return snapshot;
    }

    private async Task AddOnUiAsync(InstallJob job)
    {
        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() => Jobs.Add(job));
    }

    private Task UpdateAsync(
        InstallJob job,
        Action<InstallJob> update)
    {
        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        return _ui.RunAsync(() => update(job));
    }

    private async Task UpdateAndPersistAsync(
        InstallJob job,
        Action<InstallJob> update)
    {
        await UpdateAsync(
            job,
            update);

        await PersistAsync();
    }

    private async Task<InstallJob?> GetNextWaitingAsync()
    {
        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        InstallJob? next = null;
        await _ui.RunAsync(() =>
        {
            next = Jobs.FirstOrDefault(x => x.Status == InstallStatus.Waiting);
        });

        return next;
    }

    private async Task ReindexAsync()
    {
        if (_ui is null)
            throw new InvalidOperationException("UI dispatcher is not attached.");

        await _ui.RunAsync(() =>
        {
            for (var index = 0; index < Jobs.Count; index++)
                Jobs[index].QueuePosition = index + 1;
        });
    }

    private async Task PersistAsync()
    {
        if (_ui is null)
            return;

        InstallJob[] snapshot = [];
        await _ui.RunAsync(() => snapshot = Jobs.ToArray());
        await _store.SaveAsync(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _reconciliation;
        try { await _scheduler.DrainAsync(); } catch (OperationCanceledException) { }
        await PersistAsync();
        await _server.DisposeAsync();
        _installed.Dispose();
        _lifetime.Dispose();
    }
}


public sealed class QueueErrorEventArgs : EventArgs
{
    public string Title { get; }
    public string Message { get; }
    public InstallJob? Job { get; }

    public QueueErrorEventArgs(
        string title,
        string message,
        InstallJob? job)
    {
        Title = title;
        Message = message;
        Job = job;
    }
}
