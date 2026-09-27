using System.Text.Json;
using PkgRelay.Models;

namespace PkgRelay.Services;

public sealed class QueueStoreService
{
    private readonly string _path;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public QueueStoreService(SettingsService settings)
    {
        _path = Path.Combine(settings.DataDirectory, "queue.json");
    }

    public async Task SaveAsync(IEnumerable<InstallJob> jobs)
    {
        var persisted = jobs
            .Where(x => x.Status is not InstallStatus.Completed)
            .Select(x => new QueueEntry
            {
                Id = x.Id,
                SourceKind = x.SourceKind,
                FilePath = x.FilePath,
                RemoteUrl = x.RemoteUrl,
                PackageUri = x.PackageUri,
                DisplayName = x.DisplayName,
                TitleId = x.TitleId,
                ContentId = x.ContentId,
                Kind = x.Kind,
                FileSize = x.FileSize,
                Category = x.Category,
                AppVersion = x.AppVersion,
                Version = x.Version,
                RequiredFirmware = x.RequiredFirmware,
                Status = x.Status,
                Progress = x.Progress,
                Message = x.Message,
                TaskId = x.TaskId
            })
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var temporary = _path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                persisted,
                JsonOptions);
        }

        File.Move(temporary, _path, true);
    }

    public async Task<IReadOnlyList<InstallJob>> LoadAsync()
    {
        if (!File.Exists(_path))
            return [];

        try
        {
            await using var stream = File.OpenRead(_path);

            var entries =
                await JsonSerializer.DeserializeAsync<List<QueueEntry>>(
                    stream,
                    JsonOptions)
                ?? [];

            var jobs = new List<InstallJob>();

            foreach (var item in entries)
            {
                if (item.SourceKind == InstallSourceKind.LocalFile &&
                    !File.Exists(item.FilePath))
                {
                    continue;
                }

                if (item.SourceKind == InstallSourceKind.RemoteUrl &&
                    !Uri.TryCreate(item.RemoteUrl, UriKind.Absolute, out _))
                {
                    continue;
                }

                var restoredStatus =
                    NormalizeRestoredStatus(item);

                jobs.Add(new InstallJob
                {
                    Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                    SourceKind = item.SourceKind,
                    FilePath = item.FilePath,
                    RemoteUrl = item.RemoteUrl,
                    PackageUri = item.PackageUri,
                    DisplayName = item.DisplayName,
                    TitleId = item.TitleId,
                    ContentId = item.ContentId,
                    Kind = item.Kind,
                    FileSize = item.FileSize,
                    Category = item.Category,
                    AppVersion = item.AppVersion,
                    Version = item.Version,
                    RequiredFirmware = item.RequiredFirmware,
                    Status = restoredStatus,
                    Progress = Math.Clamp(item.Progress, 0, 100),
                    Message = RestoredMessage(item, restoredStatus),
                    TaskId = item.TaskId
                });
            }

            return jobs;
        }
        catch
        {
            return [];
        }
    }

    private static InstallStatus NormalizeRestoredStatus(
        QueueEntry entry)
    {
        return entry.Status switch
        {
            InstallStatus.Installing or
            InstallStatus.Recovering or
            InstallStatus.Finalizing
                when entry.TaskId.HasValue ||
                     !string.IsNullOrWhiteSpace(entry.ContentId)
                => InstallStatus.Recovering,

            InstallStatus.Paused
                when entry.TaskId.HasValue
                => InstallStatus.Paused,

            InstallStatus.Preparing or
            InstallStatus.Sending
                => InstallStatus.Waiting,

            InstallStatus.Completed
                => InstallStatus.Completed,

            InstallStatus.Cancelled
                => InstallStatus.Cancelled,

            InstallStatus.Failed
                => InstallStatus.Failed,

            _ => InstallStatus.Waiting
        };
    }

    private static string RestoredMessage(
        QueueEntry entry,
        InstallStatus status)
    {
        return status switch
        {
            InstallStatus.Recovering =>
                "Recovering interrupted PS4 task",

            InstallStatus.Paused =>
                "Paused task restored",

            InstallStatus.Waiting
                when entry.Status is
                    InstallStatus.Preparing or
                    InstallStatus.Sending
                => "Interrupted before PS4 accepted task; ready to retry",

            InstallStatus.Failed =>
                string.IsNullOrWhiteSpace(entry.Message)
                    ? "Failed"
                    : entry.Message,

            InstallStatus.Cancelled =>
                "Cancelled",

            _ =>
                string.IsNullOrWhiteSpace(entry.Message)
                    ? "Restored"
                    : entry.Message
        };
    }

    private sealed class QueueEntry
    {
        public Guid Id { get; set; }
        public InstallSourceKind SourceKind { get; set; }
        public string FilePath { get; set; } = "";
        public string RemoteUrl { get; set; } = "";
        public string PackageUri { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string TitleId { get; set; } = "";
        public string ContentId { get; set; } = "";
        public PkgKind Kind { get; set; }
        public long FileSize { get; set; }
        public string Category { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string Version { get; set; } = "";
        public string RequiredFirmware { get; set; } = "";
        public InstallStatus Status { get; set; } = InstallStatus.Waiting;
        public double Progress { get; set; }
        public string Message { get; set; } = "";
        public int? TaskId { get; set; }
    }
}
