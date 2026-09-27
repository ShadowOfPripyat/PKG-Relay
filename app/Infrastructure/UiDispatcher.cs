using Microsoft.UI.Dispatching;

namespace PkgRelay.Infrastructure;

public sealed class UiDispatcher
{
    private readonly DispatcherQueue _dispatcher;

    public UiDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task RunAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            completion.SetException(
                new InvalidOperationException("Unable to dispatch operation to the UI thread."));
        }

        return completion.Task;
    }
}
