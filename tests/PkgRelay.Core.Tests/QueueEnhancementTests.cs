using System.Diagnostics;
using System.Text.Json;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class QueueEnhancementTests
{
    [Fact]
    public async Task PollingBudgetIsSharedAndRequestsNeverOverlap()
    {
        var limiter = new ProgressRequestLimiter();
        var starts = new List<long>();
        var active = 0;
        var peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => limiter.ExecuteAsync(() => 200, async () =>
        {
            starts.Add(Stopwatch.GetTimestamp());
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            await Task.Delay(35);
            Interlocked.Decrement(ref active);
            return 0;
        }, CancellationToken.None)));
        Assert.Equal(1, peak);
        Assert.Equal(4, starts.Count);
        for (var i = 1; i < starts.Count; i++)
            Assert.True(Stopwatch.GetElapsedTime(starts[i - 1], starts[i]).TotalMilliseconds >= 180);
    }

    [Fact]
    public async Task CancelledQueuedPollDoesNotSendAndDoesNotBlockLaterPolls()
    {
        var limiter = new ProgressRequestLimiter();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = limiter.ExecuteAsync(() => 1, () => release.Task, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var sent = false;
        var second = limiter.ExecuteAsync(() => 1, () => { sent = true; return Task.FromResult(0); }, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult(1);
        Assert.Equal(1, await first);
        Assert.False(sent);
        Assert.Equal(2, await limiter.ExecuteAsync(() => 1, () => Task.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task FailedProgressRequestReleasesSharedBudget()
    {
        var limiter = new ProgressRequestLimiter();
        await Assert.ThrowsAsync<IOException>(() => limiter.ExecuteAsync<int>(() => 1,
            () => throw new IOException("Disconnected"), CancellationToken.None));
        Assert.Equal(42, await limiter.ExecuteAsync(() => 1, () => Task.FromResult(42), CancellationToken.None));
    }

    [Fact]
    public void OldSettingsKeepTheirPollingIntervalAndEnableFourQueueSlots()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"PollIntervalMs\":2250}")!;
        Assert.Equal(ProgressUpdateRate.Default, settings.ProgressUpdateRate);
        Assert.Equal(2250, settings.EffectivePollIntervalMs);
        Assert.Equal(4, settings.ConsoleQueueLimit);
        settings.ProgressUpdateRate = (ProgressUpdateRate)999;
        Assert.Equal(2250, settings.EffectivePollIntervalMs);
    }

    [Theory]
    [InlineData(ProgressUpdateRate.Default, 1750)]
    [InlineData(ProgressUpdateRate.Fast, 500)]
    [InlineData(ProgressUpdateRate.Realtime, 250)]
    public async Task QueueAndPollingPreferencesSurviveSettingsSave(ProgressUpdateRate rate, int interval)
    {
        var directory = Path.Combine(Path.GetTempPath(), "queue-settings-" + Guid.NewGuid());
        try
        {
            var settings = new SettingsService(directory);
            settings.Current.ConsoleQueueLimit = 12;
            settings.Current.PollIntervalMs = 1750;
            settings.Current.ProgressUpdateRate = rate;
            await settings.SaveAsync();
            var restored = new SettingsService(directory);
            await restored.LoadAsync();
            Assert.Equal(12, restored.Current.ConsoleQueueLimit);
            Assert.Equal(rate, restored.Current.ProgressUpdateRate);
            Assert.Equal(interval, restored.Current.EffectivePollIntervalMs);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task EmptyQueueCanStartRepeatedlyWithoutLeavingSchedulerRunning()
    {
        var scheduler = new PkgQueueScheduler(() => Task.FromResult(Array.Empty<InstallJob>()), () => 4,
            (_, _) => throw new InvalidOperationException("No job should be submitted"));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => scheduler.RunAsync(true, CancellationToken.None)));
        Assert.False(scheduler.IsRunning);
        await scheduler.RunAsync(true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(scheduler.IsRunning);
    }
}

