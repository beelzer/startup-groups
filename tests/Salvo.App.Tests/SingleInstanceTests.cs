using System;
using System.Threading;
using System.Threading.Tasks;
using Salvo.App.Services;
using Xunit;

namespace Salvo.App.Tests;

/// <summary>
/// Pins the single-instance contract: a duplicate launch is refused and wakes
/// the primary; a deliberate handoff (elevation / post-update restart) waits
/// for the predecessor's release; a crashed predecessor's abandoned mutex is
/// still acquirable. Unique per-test object names keep these tests from ever
/// colliding with a genuinely running Salvo instance on a dev machine.
/// </summary>
[Collection("SingleInstance")]
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _mutexName = $@"Local\SalvoTest.Mutex.{Guid.NewGuid():N}";
    private readonly string _eventName = $@"Local\SalvoTest.Show.{Guid.NewGuid():N}";

    public void Dispose() => SingleInstance.Release();

    [Fact]
    public void SecondInstance_IsRefused_AndPrimaryIsSignalled()
    {
        Assert.True(SingleInstance.TryAcquire(waitForPredecessor: false, _mutexName, _eventName));

        using var shown = new ManualResetEventSlim(initialState: false);
        SingleInstance.ListenForShowRequests(shown.Set);

        // A second launch (no handoff flag) must be refused outright…
        Assert.False(SingleInstance.TryAcquire(waitForPredecessor: false, _mutexName, _eventName));

        // …and its signal must reach the primary's show-listener.
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "show-request signal never arrived");
    }

    [Fact]
    public async Task Handoff_AcquiresOnceThePredecessorReleases()
    {
        // Acquire and release on ONE dedicated thread, exactly like production
        // (Main acquires, OnExit releases, both on the main thread) — mutex
        // ownership is per-thread, and xunit's async continuations hop threads.
        using var acquired = new ManualResetEventSlim(initialState: false);
        using var releaseRequested = new ManualResetEventSlim(initialState: false);
        var owner = new Thread(() =>
        {
            SingleInstance.TryAcquire(waitForPredecessor: false, _mutexName, _eventName);
            acquired.Set();
            releaseRequested.Wait();
            SingleInstance.Release();
        });
        owner.Start();
        acquired.Wait();

        // A handoff relaunch waits (on another thread — mutexes are reentrant
        // per-thread, so an in-proc same-thread wait would trivially succeed).
        var handoff = Task.Run(() => SingleInstance.TryAcquire(waitForPredecessor: true, _mutexName, _eventName));

        // Give the waiter time to actually block, then release the "old" instance.
        var early = await Task.WhenAny(handoff, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(handoff, early); // handoff must still be waiting
        releaseRequested.Set();
        owner.Join();

        Assert.True(await handoff.WaitAsync(TimeSpan.FromSeconds(5)), "handoff never acquired after release");
    }

    [Fact]
    public void Handoff_AcquiresAnAbandonedMutex()
    {
        // Simulate a crashed predecessor: a thread takes the mutex and dies
        // without releasing, which abandons it.
        var predecessor = new Thread(() => _ = new Mutex(initiallyOwned: true, _mutexName, out _));
        predecessor.Start();
        predecessor.Join();

        Assert.True(SingleInstance.TryAcquire(waitForPredecessor: true, _mutexName, _eventName));
    }
}
