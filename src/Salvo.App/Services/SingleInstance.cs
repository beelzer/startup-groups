using System;
using System.Threading;
using Salvo.Core.Branding;
using Salvo.Core.Services;

namespace Salvo.App.Services;

/// <summary>
/// Process-wide single-instance guard. The first instance owns a named mutex
/// and listens on a named auto-reset event; any later launch signals that
/// event (asking the primary to surface its main window) and exits.
/// Deliberate relaunches — elevation via <see cref="ProcessElevation"/>, a
/// post-update restart — pass <c>waitForPredecessor: true</c> and instead wait
/// briefly for the dying predecessor to release the mutex, so the handoff
/// doesn't get misread as a duplicate launch.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showSignal;
    private static RegisteredWaitHandle? _registeredWait;

    /// <summary>
    /// Attempts to become the primary instance. Returns false when another
    /// instance already runs (after signalling it to show its window) — the
    /// caller should exit without any WPF initialization.
    /// Must be called on the main thread; ownership is released there too.
    /// </summary>
    public static bool TryAcquire(bool waitForPredecessor) =>
        TryAcquire(waitForPredecessor, AppIdentifiers.SingleInstanceMutexName, AppIdentifiers.SingleInstanceShowEventName);

    /// <summary>Name-parameterized core; tests use unique names so they cannot collide with a live instance.</summary>
    internal static bool TryAcquire(bool waitForPredecessor, string mutexName, string eventName)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        var owned = createdNew;

        if (!owned && waitForPredecessor)
        {
            try
            {
                owned = mutex.WaitOne(Timeouts.SingleInstanceHandoffWait);
            }
            catch (AbandonedMutexException)
            {
                // Predecessor exited without releasing (the normal case for a
                // process-death handoff) — ownership is still granted.
                owned = true;
            }
        }

        if (!owned)
        {
            SignalExistingInstance(eventName);
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        // Create the show-event eagerly so a second instance launched moments
        // from now has something to signal, even before the tray exists;
        // ListenForShowRequests attaches the callback once the UI is ready.
        _showSignal = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, eventName);
        return true;
    }

    /// <summary>
    /// Starts servicing show-requests from later launches. <paramref name="onShowRequested"/>
    /// runs on a thread-pool thread — marshal to the dispatcher inside it.
    /// </summary>
    public static void ListenForShowRequests(Action onShowRequested)
    {
        if (_showSignal is null)
        {
            return;
        }

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => onShowRequested(),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Releases ownership at shutdown. Idempotent; a crash releases implicitly
    /// (the mutex is abandoned and the next launch acquires it).
    /// </summary>
    public static void Release()
    {
        _registeredWait?.Unregister(null);
        _registeredWait = null;
        _showSignal?.Dispose();
        _showSignal = null;

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread / not owned — disposal below still
                // drops our handle, which is all that matters at shutdown.
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static void SignalExistingInstance(string eventName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Primary is mid-startup or mid-shutdown; nothing to wake. The
            // user can click the tray icon — better than a duplicate instance.
        }
    }
}
