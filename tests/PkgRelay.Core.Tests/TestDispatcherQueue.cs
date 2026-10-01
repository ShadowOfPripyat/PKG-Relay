// Only the UI dispatch boundary is replaced in queue integration tests.
// The production queue, disk persistence, clients and HTTP server run unchanged.
namespace Microsoft.UI.Dispatching;

public sealed class DispatcherQueue
{
    public bool HasThreadAccess => true;
    public bool TryEnqueue(Action action) { action(); return true; }
}
