using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Tests.Fakes;
using Periphery.Usb;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Teardown races (#262 — the defect #259 reported in <c>DeviceProxyBase</c>, one layer down).
/// <para>
/// <see cref="TreehopperBoard.DisposeAsync"/> cancels only the report producer — the sole
/// holder of <c>_cts</c>'s token — and never takes <c>_comsLock</c>. Every other caller
/// (reconcile, transaction, lease teardown, LED flush) passes its own token, so callers are
/// still in flight when dispose returns: one may hold the semaphore inside a transfer, and
/// more may be parked in <c>WaitAsync</c> behind it. Disposing the semaphore underneath them
/// turns an ordinary teardown into an <see cref="ObjectDisposedException"/> thrown inside work
/// that is frequently detached, and so has no caller to catch it.
/// </para>
/// </summary>
public class BoardTeardownTests
{
    private static DeviceInfo Info() => new()
    {
        Id   = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test Treehopper",
    };

    private static TreehopperBoard BoardOver(FakeUsbBackend b)
        => TreehopperBoard.CreateForTest(Info(), UsbDevice.CreateForTest(Info(), b));

    private static T GetPrivateField<T>(TreehopperBoard board, string name)
        => (T)typeof(TreehopperBoard)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(board)!;

    [Fact]
    public async Task Dispose_LeavesComsLockAndCtsUsable()
    {
        var board = BoardOver(new FakeUsbBackend());

        await board.DisposeAsync();

        // Neither field may be disposed: in-flight callers still reach both, and there is
        // nothing to gain by disposing them (SemaphoreSlim.Dispose is only required once
        // AvailableWaitHandle has been touched; _cts is a plain, already-cancelled source).
        var comsLock = GetPrivateField<SemaphoreSlim>(board, "_comsLock");
        Assert.True(await comsLock.WaitAsync(TimeSpan.FromSeconds(1)));
        comsLock.Release();

        var cts = GetPrivateField<CancellationTokenSource>(board, "_cts");
        Assert.True(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_WithCallerHoldingAndAnotherParkedOnComsLock_BothUnwindCleanly()
    {
        // The reported shape: one caller inside a transfer holding the coms lock, another
        // queued behind it, and the board disposed underneath both.
        // The gate is the test's own, handed to the fake as a one-line hook: the fake
        // gains no gate protocol, and every other test that uses it is untouched because
        // the hook is null by default.
        var gate = new TaskCompletionSource();
        var reachedGate = new TaskCompletionSource();
        var b = new FakeUsbBackend
        {
            // Honours ct, as a real backend does: a cancelled transfer must not sail on
            // just because the gate is still shut.
            OnBulkWrite = ct => { reachedGate.TrySetResult(); return gate.Task.WaitAsync(ct); },
        };
        var board = BoardOver(b);

        // Holder: enters the lock and parks inside the bulk write.
        var holder = board.SetLedAsync(true);
        await reachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Queued: parks in _comsLock.WaitAsync behind the holder. Deliberately the SAME
        // reconcile as the holder, so that once it acquires the lock it plans no commands
        // and touches the (by then disposed) transport not at all. That isolates the
        // semaphore, which is what this test is about — a differing reconcile would write,
        // and fail on the disposed UsbDevice for an unrelated reason.
        var queued = board.SetLedAsync(true);

        // Dispose now WAITS for the coms-lock holder (#263 item 2), so run it concurrently
        // and let the holder finish -- otherwise this sits out the full bound for no
        // reason. The parked-caller race it exists to cover is unaffected: both callers
        // are still in place when disposal begins.
        var disposing = board.DisposeAsync().AsTask();
        gate.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));

        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await queued.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task Dispose_WaitsForAnInFlightTransferBeforeTearingDownTheTransport()
    {
        // #263 item 2. #262 stopped dispose from disposing the semaphore under parked
        // callers; this stops it disposing the TRANSPORT under a caller that is mid
        // transfer. Ordering is the whole assertion: the backend must not see DisposeAsync
        // until the write that was in flight has returned.
        var gate = new TaskCompletionSource();
        var reachedGate = new TaskCompletionSource();
        var writeReturned = false;
        var disposedWhileWriteInFlight = false;

        var b = new FakeUsbBackend();
        b.OnBulkWrite = async ct =>
        {
            reachedGate.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            disposedWhileWriteInFlight = b.Disposed;   // sampled before the write returns
            writeReturned = true;
        };

        var board = BoardOver(b);

        var holder = board.SetLedAsync(true);
        await reachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = board.DisposeAsync().AsTask();

        // Dispose is parked on the coms lock, so the transport is still alive.
        Assert.False(b.Disposed);

        gate.SetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(writeReturned);
        Assert.False(disposedWhileWriteInFlight);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task Dispose_AfterTheComsLockWaitExpires_AQueuedCallerLeavesTheDisposedTransportAlone()
    {
        // #263 item 2, the TIMEOUT path. Dispose's wait for the coms lock is bounded and
        // proceeds on expiry, so a wedged holder can still be inside a transfer when the
        // transport is torn down — and the caller queued behind it acquires the lock only
        // afterwards, with the transport already gone.
        //
        // The backend's in-flight drain does not cover this one: the queued caller has not
        // issued a transfer yet, so there is nothing registered for the drain to wait on.
        // What covers it is the _disposed re-check each coms-lock path performs once it
        // holds the semaphore.
        //
        // Costs TransferTimeout * 2 in wall clock. That bound is the thing under test.
        var gate = new TaskCompletionSource();
        var reachedGate = new TaskCompletionSource();
        var wroteAfterTransportDisposed = false;

        var b = new FakeUsbBackend();
        b.OnBulkWrite = _ =>
        {
            if (b.Disposed)
                wroteAfterTransportDisposed = true;

            // Only the FIRST write wedges, and it deliberately ignores ct: a holder that
            // unwinds on cancellation is not the holder dispose has to step over.
            return reachedGate.TrySetResult() ? gate.Task : Task.CompletedTask;
        };

        var board = BoardOver(b);

        // Holder: enters the coms lock and wedges inside the bulk write.
        var holder = board.SetLedAsync(true);
        await reachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Queued: parks in _comsLock.WaitAsync behind the holder, asking for a DIFFERENT
        // config so it plans a command and would genuinely write. (The same-config
        // reconcile the sibling test uses plans nothing, and would pass with or without
        // the guard.)
        var queued = board.SetLedAsync(false);

        // Waits out TransferTimeout * 2, gives up on the holder, and disposes the transport.
        await board.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(b.Disposed);

        // Now let the holder go. It releases the coms lock, the queued caller finally
        // acquires it, and must unwind quietly — writing here would hit a disposed
        // UsbDevice and raise ObjectDisposedException inside an LED flush, which is
        // routinely detached and so has nobody to catch it (#259, #262).
        gate.SetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await queued.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(wroteAfterTransportDisposed);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var board = BoardOver(new FakeUsbBackend());

        await board.DisposeAsync();
        await board.DisposeAsync();
    }
}
