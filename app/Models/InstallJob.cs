using PkgRelay.Infrastructure;

namespace PkgRelay.Models;

public enum InstallSourceKind
{
    LocalFile,
    RemoteUrl
}

public enum InstallStatus
{
    // Values 0-7 are kept stable for queue.json compatibility with v0.4.x.
    Waiting = 0,
    Preparing = 1,
    Sending = 2,
    Installing = 3,
    Paused = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,

    Recovering = 8,
    Finalizing = 9
}

public sealed class InstallJob : ObservableObject
{
    private InstallStatus _status = InstallStatus.Waiting;
    private double _progress;
    private string _message = "";
    private int? _taskId;
    private int _queuePosition;
    private double _transferRateBytesPerSecond;
    private int? _etaSeconds;
    private CancellationTokenSource _cancellation = new();

    public Guid Id { get; set; } = Guid.NewGuid();
    public InstallSourceKind SourceKind { get; set; }

    public string FilePath { get; set; } = "";
    public string RemoteUrl { get; set; } = "";

    /// <summary>
    /// Exact URL handed to RPI for this job. Persisted so an interrupted local
    /// transfer can rebind the same endpoint after an application restart.
    /// </summary>
    public string PackageUri { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string ContentId { get; set; } = "";
    public PkgKind Kind { get; set; } = PkgKind.Unknown;
    public long FileSize { get; set; }

    public string Category { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Version { get; set; } = "";
    public string RequiredFirmware { get; set; } = "";

    public string SizeText => FileSize <= 0 ? "Remote" : FormatSize(FileSize);

    public string KindText => Kind switch
    {
        PkgKind.Base => "Base",
        PkgKind.Update => "Update",
        PkgKind.Dlc => "DLC",
        PkgKind.Homebrew => "Homebrew",
        _ => "Package"
    };

    public string StatusText => Status switch
    {
        InstallStatus.Recovering => "Recovering",
        InstallStatus.Finalizing => "Finalizing",
        _ => Status.ToString()
    };

    public string VersionText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AppVersion))
                return $"v{AppVersion}";

            if (!string.IsNullOrWhiteSpace(Version))
                return $"v{Version}";

            return "";
        }
    }

    public string GroupKey =>
        !string.IsNullOrWhiteSpace(TitleId)
            ? TitleId
            : Id.ToString("N");

    public string PackageIdentity
    {
        get
        {
            var pieces = new[]
            {
                TitleId,
                KindText,
                VersionText,
                string.IsNullOrWhiteSpace(RequiredFirmware)
                    ? ""
                    : $"FW {RequiredFirmware}"
            }.Where(x => !string.IsNullOrWhiteSpace(x));

            return string.Join(" • ", pieces);
        }
    }

    public int QueuePosition
    {
        get => _queuePosition;
        set
        {
            if (Set(ref _queuePosition, value))
                Raise(nameof(QueuePositionText));
        }
    }

    public string QueuePositionText => $"#{QueuePosition}";

    public int? TaskId
    {
        get => _taskId;
        set
        {
            if (Set(ref _taskId, value))
            {
                Raise(nameof(CanPause));
                Raise(nameof(CanResume));
            }
        }
    }

    public InstallStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value))
                return;

            Raise(nameof(StatusText));
            Raise(nameof(CanPause));
            Raise(nameof(CanResume));
            Raise(nameof(CanCancel));
            Raise(nameof(CanMove));
            Raise(nameof(CanRetry));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value))
                Raise(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Progress:0.0}%";

    public double TransferRateBytesPerSecond
    {
        get => _transferRateBytesPerSecond;
        set
        {
            if (Set(ref _transferRateBytesPerSecond, Math.Max(0, value)))
                Raise(nameof(TransferDetailsText));
        }
    }

    public int? EtaSeconds
    {
        get => _etaSeconds;
        set
        {
            var normalized = value is < 0 ? null : value;
            if (Set(ref _etaSeconds, normalized))
                Raise(nameof(TransferDetailsText));
        }
    }

    public string TransferDetailsText
    {
        get
        {
            var pieces = new List<string>(2);

            if (TransferRateBytesPerSecond > 0)
                pieces.Add($"{FormatSize((long)TransferRateBytesPerSecond)}/s");

            if (EtaSeconds is > 0)
                pieces.Add(FormatDuration(EtaSeconds.Value));

            return string.Join(" • ", pieces);
        }
    }

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public bool CanPause =>
        TaskId.HasValue &&
        Status == InstallStatus.Installing;

    public bool CanResume =>
        TaskId.HasValue &&
        Status == InstallStatus.Paused;

    public bool CanCancel =>
        Status is not InstallStatus.Completed
            and not InstallStatus.Cancelled
            and not InstallStatus.Failed;

    public bool CanMove =>
        Status == InstallStatus.Waiting;

    public bool CanRetry =>
        Status is InstallStatus.Failed
            or InstallStatus.Cancelled;

    internal CancellationTokenSource Cancellation => _cancellation;

    internal void ResetForRetry()
    {
        if (_cancellation.IsCancellationRequested)
        {
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
        }

        TaskId = null;
        PackageUri = "";
        Progress = 0;
        TransferRateBytesPerSecond = 0;
        EtaSeconds = null;
        Message = "Waiting";
        Status = InstallStatus.Waiting;
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes}m {duration.Seconds}s";

        return $"{duration.Seconds}s";
    }
}
