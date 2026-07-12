namespace Curia.Helpers;

/// <summary>
/// Shares a one-time asynchronous initialization operation between callers.
/// A failed operation is not cached, allowing a later page visit to retry it.
/// </summary>
public sealed class AsyncInitializationGate
{
    private readonly object _lock = new();
    private Task? _initializationTask;
    private bool _isInitialized;

    public Task EnsureAsync(Func<Task> initializeAsync)
    {
        lock (_lock)
        {
            if (_isInitialized)
                return Task.CompletedTask;

            return _initializationTask ??= InitializeAndTrackAsync(initializeAsync);
        }
    }

    private async Task InitializeAndTrackAsync(Func<Task> initializeAsync)
    {
        try
        {
            await initializeAsync();
            lock (_lock)
                _isInitialized = true;
        }
        finally
        {
            lock (_lock)
                _initializationTask = null;
        }
    }
}