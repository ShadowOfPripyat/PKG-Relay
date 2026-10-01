using PkgRelay.Models;

namespace PkgRelay.Services;

// Registration is awaited in queue order; monitoring runs independently. The
// shared loop can also accept new work while restored or paused tasks are live.
internal sealed class PkgQueueScheduler
{
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Func<Task<InstallJob[]>> snapshot;
    private readonly Func<int> capacity;
    private readonly Func<InstallJob, CancellationToken, Task<Task>> start;
    private Task? loop;
    private bool submitWaiting;

    internal PkgQueueScheduler(Func<Task<InstallJob[]>> snapshot, Func<int> capacity,
        Func<InstallJob, CancellationToken, Task<Task>> start)
    {
        this.snapshot = snapshot;
        this.capacity = capacity;
        this.start = start;
    }

    internal Task RunAsync(bool includeWaiting, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            submitWaiting |= includeWaiting;
            Pulse();
            return loop ??= RunCoreAsync(token);
        }
    }

    internal void Pulse()
    {
        lock (gate)
            if (wake.CurrentCount == 0) wake.Release();
    }

    internal Task DrainAsync()
    {
        lock (gate) return loop ?? Task.CompletedTask;
    }

    internal bool IsRunning { get { lock (gate) return loop is not null; } }

    private async Task RunCoreAsync(CancellationToken token)
    {
        // Do not finish before RunAsync has assigned the task under the lock.
        await Task.Yield();
        var active = new Dictionary<Guid, Task>();
        var finishedNormally = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var id in active.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
                {
                    var finished = active[id];
                    active.Remove(id);
                    await finished;
                }

                var jobs = await snapshot();
                bool allowWaiting;
                lock (gate) allowWaiting = submitWaiting;
                var next = jobs.OrderBy(x => x.QueuePosition).FirstOrDefault(x =>
                    !active.ContainsKey(x.Id) &&
                    (x.Status is InstallStatus.Recovering or InstallStatus.Paused ||
                     (allowWaiting && x.Status == InstallStatus.Waiting && !WaitsForBase(x, jobs))));

                if (next is not null && active.Count < Math.Clamp(capacity(), 1, 32))
                {
                    active.Add(next.Id, await start(next, token));
                    continue;
                }

                lock (gate)
                {
                    // A concurrent Start/settings change may have arrived while
                    // taking the snapshot. Consume its wake before going idle.
                    if (active.Count == 0 && wake.CurrentCount == 0)
                    {
                        loop = null;
                        submitWaiting = false;
                        finishedNormally = true;
                        return;
                    }
                }

                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
                var changed = wake.WaitAsync(waiting.Token);
                if (active.Count == 0)
                    await changed;
                else
                    await Task.WhenAny(changed, Task.WhenAny(active.Values));
                waiting.Cancel();
                try { await changed; } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
            }
        }
        finally
        {
            // On app shutdown every worker receives the same lifetime token.
            // Keep the file server alive until all workers leave their cleanup.
            try { await Task.WhenAll(active.Values); }
            finally
            {
                if (!finishedNormally) lock (gate)
                {
                    loop = null;
                    submitWaiting = false;
                }
            }
        }
    }

    private static bool WaitsForBase(InstallJob job, InstallJob[] jobs) =>
        job.Kind is PkgKind.Dlc or PkgKind.Update && !string.IsNullOrWhiteSpace(job.TitleId) &&
        jobs.Any(x => x.Id != job.Id && x.Kind == PkgKind.Base &&
            string.Equals(x.TitleId, job.TitleId, StringComparison.OrdinalIgnoreCase) &&
            x.Status is InstallStatus.Waiting or InstallStatus.Preparing or InstallStatus.Sending or
                InstallStatus.Installing or InstallStatus.Paused or InstallStatus.Recovering or InstallStatus.Finalizing);
}
