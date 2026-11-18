namespace NotchPC.App.Infrastructure;

/// <summary>
/// One NotchPC per user session. A second launch signals the first one (which then opens its settings) and exits.
/// The first instance waits on a kernel event, so there is no polling.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\NotchPC.SingleInstance";
    private const string EventName = @"Local\NotchPC.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <returns>The guard for the first instance, or null after signalling the already running one.</returns>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!createdNew)
        {
            activate.Set();
            activate.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, activate);
    }

    public void OnActivationRequested(Action callback)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
