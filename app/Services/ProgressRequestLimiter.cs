using System.Diagnostics;

namespace PkgRelay.Services;

// One console-wide budget, rather than an independent fast timer for every DLC.
internal sealed class ProgressRequestLimiter
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long lastStart;

    internal async Task<T> ExecuteAsync<T>(Func<int> intervalMs, Func<Task<T>> request,
        CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (lastStart != 0)
            {
                var remaining = intervalMs() - Stopwatch.GetElapsedTime(lastStart).TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
            }
            token.ThrowIfCancellationRequested();
            lastStart = Stopwatch.GetTimestamp();
            return await request();
        }
        finally { gate.Release(); }
    }
}
