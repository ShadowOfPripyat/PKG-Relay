using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class QueuePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QueueTests-" + Guid.NewGuid());

    [Fact]
    public async Task ConcurrentSavesLeaveACompleteRecoverableQueue()
    {
        var store = new QueueStoreService(_directory);
        var jobs = Enumerable.Range(0, 400).Select(index => new InstallJob
        {
            SourceKind = InstallSourceKind.RemoteUrl,
            RemoteUrl = $"http://127.0.0.1/game{index}.pkg",
            DisplayName = new string('x', 1024),
            Status = InstallStatus.Installing,
            TaskId = index,
            QueuePosition = index + 1
        }).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.SaveAsync(jobs)));
        var restored = await store.LoadAsync();
        Assert.Equal(jobs.Length, restored.Count);
        Assert.All(restored, job => Assert.Equal(InstallStatus.Recovering, job.Status));
        Assert.Equal(jobs.Select(x => x.Id), restored.Select(x => x.Id));
        Assert.False(File.Exists(Path.Combine(_directory, "queue.json.tmp")));
    }

    [Theory]
    [InlineData(InstallStatus.Preparing, InstallStatus.Waiting)]
    [InlineData(InstallStatus.Sending, InstallStatus.Recovering)]
    [InlineData(InstallStatus.Installing, InstallStatus.Recovering)]
    [InlineData(InstallStatus.Finalizing, InstallStatus.Recovering)]
    [InlineData(InstallStatus.Paused, InstallStatus.Paused)]
    public async Task InterruptedStatesRestoreWithoutResubmittingAcceptedTasks(
        InstallStatus saved, InstallStatus expected)
    {
        var store = new QueueStoreService(_directory);
        var job = new InstallJob
        {
            SourceKind = InstallSourceKind.RemoteUrl,
            RemoteUrl = "http://127.0.0.1/game.pkg",
            ContentId = "UP0000-CUSA12345_00-TESTCONTENT000001",
            Status = saved,
            TaskId = saved == InstallStatus.Paused ? 42 : null
        };
        await store.SaveAsync([job]);
        var restored = Assert.Single(await store.LoadAsync());
        Assert.Equal(expected, restored.Status);
        Assert.Equal(job.ContentId, restored.ContentId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
