using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PkgRelay.Infrastructure;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class PackageSourceLifetimeTests
{
    [Fact]
    public async Task ConfirmedConsoleRetryReconcilesFailedJobWithoutResubmission()
    {
        await using var fixture = await Fixture.Create();
        fixture.EnableVerification();
        var job = await fixture.FailInstall();
        fixture.ConfirmInstalled = true;
        Assert.True(await fixture.Queue.CheckInstalledAsync(job));
        Assert.Equal(InstallStatus.Completed, job.Status);
        Assert.Equal(100, job.Progress);
        Assert.Single(fixture.InstallRequests);
        await fixture.AssertUnavailable(job.PackageUri);
        var saved = await new QueueStoreService(new SettingsService(fixture.Directory)).LoadAsync();
        // PKG Relay deliberately omits completed jobs from its persisted queue.
        Assert.Empty(saved);
    }

    [Fact]
    public async Task UnconfirmedInstallationPreservesFailedStateAndPackageSource()
    {
        await using var fixture = await Fixture.Create();
        fixture.EnableVerification();
        var job = await fixture.FailInstall();
        Assert.False(await fixture.Queue.CheckInstalledAsync(job));
        Assert.Equal(InstallStatus.Failed, job.Status);
        await fixture.AssertAvailable(job.PackageUri);
    }

    [Fact]
    public async Task ConfirmedInstallationStopsStaleProgressWorker()
    {
        await using var fixture = await Fixture.Create();
        fixture.EnableVerification(); fixture.HoldDownloads = true;
        var job = await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        var run = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => job.Status == InstallStatus.Installing);
        fixture.ConfirmInstalled = true;
        Assert.True(await fixture.Queue.CheckInstalledAsync(job));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(InstallStatus.Completed, job.Status);
        Assert.Equal(100, job.Progress);
    }

    [Fact]
    public async Task ConsoleCanFetchPackageAndManifestAfterBgftFailure()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        Assert.Equal(InstallStatus.Failed, job.Status);
        Assert.NotNull(job.TaskId);
        await fixture.AssertAvailable(job.PackageUri);
        Assert.Single(fixture.InstallRequests);
        Assert.Empty(job.TransferDetailsText);
    }

    [Fact]
    public async Task FailedSourceIsRestoredOnRestartWithoutResubmittingInstall()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        var oldUri = job.PackageUri;
        await fixture.Restart();
        var restored = Assert.Single(fixture.Queue.Jobs);
        Assert.Equal(InstallStatus.Failed, restored.Status);
        Assert.Equal(job.TaskId, restored.TaskId);
        Assert.Equal(oldUri, restored.PackageUri);
        await fixture.AssertAvailable(oldUri);
        Assert.Single(fixture.InstallRequests);
    }

    [Fact]
    public async Task QueuedRetryKeepsOldSourceIncludingAfterRestart()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        var oldUri = job.PackageUri;
        await fixture.Queue.RetryAsync(job);
        Assert.Equal(InstallStatus.Waiting, job.Status);
        Assert.Equal(oldUri, job.PackageUri);
        await fixture.AssertAvailable(oldUri);
        await fixture.Restart();
        await fixture.AssertAvailable(oldUri);
        Assert.Single(fixture.InstallRequests);
    }

    [Fact]
    public async Task RemovingFailedJobReleasesItsSource()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        await fixture.Queue.RemoveManyAsync([job]);
        await fixture.AssertUnavailable(job.PackageUri);
    }

    [Fact]
    public async Task CancellationReleasesRetainedSource()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        job.Status = InstallStatus.Paused;
        await fixture.Queue.CancelAsync(job);
        Assert.Equal(InstallStatus.Cancelled, job.Status);
        await fixture.AssertUnavailable(job.PackageUri);
    }

    [Fact]
    public async Task SharedSourceIsRetainedUntilLastOwnerIsRemoved()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        var other = new InstallJob
        {
            FilePath = job.FilePath, PackageUri = job.PackageUri,
            SourceKind = InstallSourceKind.LocalFile, Status = InstallStatus.Failed,
            TaskId = 43
        };
        fixture.Queue.Jobs.Add(other);
        await fixture.Queue.RemoveManyAsync([job]);
        await fixture.AssertAvailable(job.PackageUri);
        await fixture.Queue.RemoveManyAsync([other]);
        await fixture.AssertUnavailable(job.PackageUri);
    }

    [Fact]
    public async Task ClearingFailedJobsReleasesTheirSources()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        await fixture.Queue.ClearFinishedAsync();
        Assert.Empty(fixture.Queue.Jobs);
        await fixture.AssertUnavailable(job.PackageUri);
    }

    [Fact]
    public async Task MissingFileDoesNotPreventOtherFailedSourcesRestoring()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.FailInstall();
        var secondPath = Path.Combine(fixture.Directory, "other.pkg");
        var bytes = (byte[])fixture.PackageBytes.Clone();
        Encoding.ASCII.GetBytes("UP0000-CUSA12346_00-OTHERCONTENT00001").CopyTo(bytes, 0x40);
        await File.WriteAllBytesAsync(secondPath, bytes);
        var second = await fixture.Queue.AddLocalAsync(secondPath);
        await fixture.Queue.StartPendingAsync();
        Assert.Equal(InstallStatus.Failed, second.Status);
        await fixture.CloseQueue();
        File.Delete(first.FilePath);
        fixture.CreateQueue();
        await fixture.Queue.RestoreAsync();
        await fixture.AssertAvailable(second.PackageUri, bytes);
        Assert.Equal(2, fixture.InstallRequests.Count);
    }

    [Fact]
    public async Task CompletedJobReleasesSource()
    {
        await using var fixture = await Fixture.Create();
        fixture.CompleteDownloads = true;
        var job = await fixture.FailInstall();
        Assert.Equal(InstallStatus.Completed, job.Status);
        await fixture.AssertUnavailable(job.PackageUri);
    }

    [Fact]
    public async Task InvalidSavedRouteCannotRemoveAnotherJobsSource()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        var damaged = new InstallJob
        {
            FilePath = job.FilePath, SourceKind = InstallSourceKind.LocalFile,
            Status = InstallStatus.Failed, PackageUri = job.PackageUri + "-invalid"
        };
        fixture.Queue.Jobs.Add(damaged);
        await fixture.Restart();
        await fixture.AssertAvailable(job.PackageUri);
    }

    [Fact]
    public async Task WaitingJobFromOlderRetryRestoresDeterministicRoute()
    {
        await using var fixture = await Fixture.Create();
        var job = await fixture.FailInstall();
        var oldUri = job.PackageUri;
        job.ResetForRetry();
        job.PackageUri = ""; // Simulate the old client's persisted Retry state.
        await fixture.Restart();
        var restored = Assert.Single(fixture.Queue.Jobs);
        Assert.Equal(InstallStatus.Waiting, restored.Status);
        Assert.Null(restored.TaskId);
        Assert.Empty(restored.PackageUri);
        await fixture.AssertAvailable(oldUri);
        Assert.Single(fixture.InstallRequests);
        await fixture.Queue.RemoveManyAsync([restored]);
        await fixture.AssertUnavailable(oldUri);
    }

    [Fact]
    public async Task QueuesMultiplePackagesAndRefillsWhileLargePackageStillRuns()
    {
        await using var fixture = await Fixture.Create();
        fixture.HoldDownloads = true;
        fixture.CurrentSettings.ConsoleQueueLimit = 2;
        fixture.CurrentSettings.ProgressUpdateRate = ProgressUpdateRate.Realtime;
        var first = await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        var second = await fixture.AddPackage(1);
        var third = await fixture.AddPackage(2);
        var run = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => fixture.InstallRequests.Count == 2 && second.Status == InstallStatus.Installing);
        Assert.Equal(InstallStatus.Installing, first.Status);
        Assert.Equal(InstallStatus.Installing, second.Status);
        Assert.Equal(InstallStatus.Waiting, third.Status);
        await fixture.AssertAvailable(first.PackageUri);
        await fixture.AssertAvailable(second.PackageUri);
        fixture.FinishedTasks[43] = true;
        await WaitUntil(() => fixture.InstallRequests.Count == 3 && third.Status == InstallStatus.Installing);
        Assert.Equal(InstallStatus.Installing, first.Status);
        Assert.Equal(InstallStatus.Completed, second.Status);
        fixture.CompleteDownloads = true;
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(fixture.Queue.Jobs, x => Assert.Equal(InstallStatus.Completed, x.Status));
    }

    [Fact]
    public async Task SequentialSettingDoesNotRegisterNextPackageEarly()
    {
        await using var fixture = await Fixture.Create();
        fixture.HoldDownloads = true;
        fixture.CurrentSettings.ConsoleQueueLimit = 1;
        fixture.CurrentSettings.ProgressUpdateRate = ProgressUpdateRate.Realtime;
        await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        var second = await fixture.AddPackage(1);
        var run = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => fixture.ProgressRequests > 1);
        Assert.Single(fixture.InstallRequests);
        Assert.Equal(InstallStatus.Waiting, second.Status);
        fixture.CompleteDownloads = true;
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, fixture.InstallRequests.Count);
    }

    [Fact]
    public async Task StartAndCapacityChangesWakeAnExistingMonitorLoop()
    {
        await using var fixture = await Fixture.Create();
        fixture.HoldDownloads = true;
        fixture.CurrentSettings.ConsoleQueueLimit = 1;
        fixture.CurrentSettings.ProgressUpdateRate = ProgressUpdateRate.Realtime;
        await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        var run = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => fixture.InstallRequests.Count == 1);
        await fixture.AddPackage(1);
        fixture.CurrentSettings.ConsoleQueueLimit = 2;
        fixture.Queue.NotifySettingsChanged();
        var anotherStart = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => fixture.InstallRequests.Count == 2);
        fixture.CompleteDownloads = true;
        await Task.WhenAll(run, anotherStart).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, fixture.InstallRequests.Count);
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Queue did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task DlcWaitsForQueuedBaseButIndependentPackagesCanProceed()
    {
        await using var fixture = await Fixture.Create();
        fixture.HoldDownloads = true;
        fixture.CurrentSettings.ProgressUpdateRate = ProgressUpdateRate.Realtime;
        var game = await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        game.Kind = PkgKind.Base;
        var dlc = await fixture.AddPackage(1);
        dlc.Kind = PkgKind.Dlc;
        dlc.TitleId = game.TitleId;
        var unrelated = await fixture.AddPackage(2);
        var run = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => unrelated.Status == InstallStatus.Installing && fixture.ProgressRequests > 1);
        Assert.Equal(InstallStatus.Waiting, dlc.Status);
        Assert.Equal(2, fixture.InstallRequests.Count);
        fixture.FinishedTasks[42] = true;
        await WaitUntil(() => dlc.Status == InstallStatus.Installing);
        Assert.Equal(InstallStatus.Completed, game.Status);
        Assert.Equal(3, fixture.InstallRequests.Count);
        fixture.CompleteDownloads = true;
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MultipleAcceptedTasksRestoreAndNewDlcCanBeAddedWithoutResubmission()
    {
        await using var fixture = await Fixture.Create();
        fixture.HoldDownloads = true;
        fixture.CurrentSettings.ProgressUpdateRate = ProgressUpdateRate.Realtime;
        var game = await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        var firstDlc = await fixture.AddPackage(1);
        firstDlc.Kind = PkgKind.Dlc;
        var originalRun = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => firstDlc.Status == InstallStatus.Installing);
        await fixture.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => originalRun);
        await WaitUntil(() => fixture.Queue.Jobs.All(x => x.Status == InstallStatus.Installing));
        Assert.Equal(2, fixture.InstallRequests.Count);
        await fixture.AssertAvailable(game.PackageUri);
        await fixture.AssertAvailable(firstDlc.PackageUri);
        var next = await fixture.AddPackage(2);
        next.Kind = PkgKind.Dlc;
        var newRun = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => next.Status == InstallStatus.Installing);
        Assert.Equal(3, fixture.InstallRequests.Count);
        fixture.CompleteDownloads = true;
        await newRun.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(fixture.Queue.Jobs, x => Assert.Equal(InstallStatus.Completed, x.Status));
    }

    [Fact]
    public async Task CancellingOneTaskRefillsItsSlotAndPreservesOtherSources()
    {
        await using var fixture = await Fixture.Create();
        fixture.HoldDownloads = true;
        fixture.CurrentSettings.ConsoleQueueLimit = 2;
        fixture.CurrentSettings.ProgressUpdateRate = ProgressUpdateRate.Realtime;
        var first = await fixture.Queue.AddLocalAsync(Path.Combine(fixture.Directory, "game.pkg"));
        var second = await fixture.AddPackage(1);
        var third = await fixture.AddPackage(2);
        var run = fixture.Queue.StartPendingAsync();
        await WaitUntil(() => second.Status == InstallStatus.Installing);
        await fixture.Queue.CancelAsync(second);
        await WaitUntil(() => third.Status == InstallStatus.Installing);
        Assert.Equal(InstallStatus.Installing, first.Status);
        await fixture.AssertAvailable(first.PackageUri);
        await fixture.AssertAvailable(third.PackageUri);
        await fixture.AssertUnavailable(second.PackageUri);
        fixture.CompleteDownloads = true;
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(InstallStatus.Cancelled, second.Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "source-lifetime-" + Guid.NewGuid());
        internal byte[] PackageBytes { get; } = CreatePackage();
        internal ConcurrentQueue<string> InstallRequests { get; } = new();
        internal bool CompleteDownloads;
        internal bool HoldDownloads;
        internal bool ConfirmInstalled;
        internal void EnableVerification()
        {
            PackageBytes.AsSpan(0x40, 0x30).Clear();
            Encoding.ASCII.GetBytes("UP0000-CUSA12345_00-BASECONTENT00001").CopyTo(PackageBytes, 0x40);
            BinaryPrimitives.WriteUInt32BigEndian(PackageBytes.AsSpan(0x74), 0x1a);
            PackageBytes.AsSpan(0xfe0, 32).Fill(0xaa);
            File.WriteAllBytes(Path.Combine(Directory, "game.pkg"), PackageBytes);
            settings.Current.ResidentRpiPort = settings.Current.RpiPort;
            settings.Current.ResidentRpiToken = "test-token";
        }
        internal int ProgressRequests;
        internal ConcurrentDictionary<int, bool> FinishedTasks { get; } = new();
        internal AppSettings CurrentSettings => settings.Current;
        internal InstallQueueService Queue = null!;
        private SettingsService settings = null!;
        private LogService log = null!;
        private Ps4RpiClient rpi = null!;
        private WebApplication console = null!;
        private bool queueClosed;
        private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false });

        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            System.IO.Directory.CreateDirectory(fixture.Directory);
            await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, "game.pkg"), fixture.PackageBytes);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            fixture.console = builder.Build();
            fixture.console.MapGet("/api/health", () => new { status = "ok", service = "Orbis Toolz" });
            fixture.console.MapPost("/api/install", async (Microsoft.AspNetCore.Http.HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body);
                fixture.InstallRequests.Enqueue(await reader.ReadToEndAsync());
                return new { status = "success", task_id = 41 + fixture.InstallRequests.Count };
            });
            fixture.console.MapPost("/api/get_task_progress", async (Microsoft.AspNetCore.Http.HttpRequest request) =>
            {
                using var document = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
                var task = document.RootElement.GetProperty("task_id").GetInt32();
                Interlocked.Increment(ref fixture.ProgressRequests);
                var complete = fixture.CompleteDownloads || fixture.FinishedTasks.ContainsKey(task);
                return new
                {
                    status = "success", error_result = complete || fixture.HoldDownloads ? 0 : unchecked((int)0x80990085),
                    transferred_total = complete ? 4096 : 0,
                    length_total = complete || fixture.HoldDownloads ? 4096 : 0,
                    rest_sec_total = complete ? 0u : uint.MaxValue, preparing_percent = 1
                };
            });
            fixture.console.MapPost("/api/unregister_task", () => new { status = "success" });
            fixture.console.MapPost("/api/get_install_status", async (Microsoft.AspNetCore.Http.HttpRequest request) =>
            {
                using var json = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
                return new { status = "success", content_id = json.RootElement.GetProperty("content_id").GetString(),
                    installed = fixture.ConfirmInstalled, installing = false };
            });
            await fixture.console.StartAsync();
            var consoleAddress = fixture.console.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture.settings = new SettingsService(fixture.Directory);
            fixture.settings.Current.Ps4Address = "127.0.0.1";
            fixture.settings.Current.LocalAddress = "127.0.0.1";
            fixture.settings.Current.RpiPort = new Uri(consoleAddress).Port;
            fixture.settings.Current.HttpPort = FreePort();
            fixture.settings.Current.AutoStartPackages = false;
            fixture.settings.Current.PollIntervalMs = 500;
            fixture.log = new LogService(fixture.settings);
            fixture.rpi = new Ps4RpiClient(fixture.settings, fixture.log);
            fixture.CreateQueue();
            return fixture;
        }

        internal void CreateQueue()
        {
            Queue = new InstallQueueService(settings, log, new NetworkService(), new PkgHttpServer(log),
                new PkgMetadataService(), new QueueStoreService(settings), rpi);
            Queue.AttachDispatcher(new UiDispatcher(new DispatcherQueue()));
            queueClosed = false;
        }

        internal async Task<InstallJob> FailInstall()
        {
            var job = await Queue.AddLocalAsync(Path.Combine(Directory, "game.pkg"));
            await Queue.StartPendingAsync();
            return job;
        }

        internal async Task<InstallJob> AddPackage(int number)
        {
            var bytes = (byte[])PackageBytes.Clone();
            Encoding.ASCII.GetBytes($"UP0000-CUSA{12345 + number:00000}_00-TESTCONTENT000001").CopyTo(bytes, 0x40);
            var path = Path.Combine(Directory, $"dlc-{number}.pkg");
            await File.WriteAllBytesAsync(path, bytes);
            return await Queue.AddLocalAsync(path);
        }

        internal async Task CloseQueue()
        {
            if (!queueClosed)
            {
                await Queue.DisposeAsync();
                queueClosed = true;
            }
        }

        internal async Task Restart()
        {
            await CloseQueue();
            CreateQueue();
            await Queue.RestoreAsync();
        }

        internal async Task AssertAvailable(string uri, byte[]? bytes = null)
        {
            bytes ??= PackageBytes;
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            using var head = await http.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(256, 511);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(bytes[256..512], await response.Content.ReadAsByteArrayAsync());
        }

        internal async Task AssertUnavailable(string uri)
        {
            using var result = await http.GetAsync(uri);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            await CloseQueue();
            rpi.Dispose();
            http.Dispose();
            await console.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }

        private static byte[] CreatePackage()
        {
            var bytes = new byte[0x1000];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x7F434E54);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0x430), (ulong)bytes.Length);
            Encoding.ASCII.GetBytes("UP0000-CUSA12345_00-TESTCONTENT000001").CopyTo(bytes, 0x40);
            return bytes;
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}

