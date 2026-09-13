using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Periphery.Usb.Tests.Fakes;

namespace Periphery.Usb.Tests;

/// <summary>
/// Covers the per-transfer watchdog added for kiosk-grade observability: a wedged
/// endpoint must fault promptly with <see cref="UsbTimeoutException"/> rather than
/// blocking forever, caller cancellation must stay distinguishable from a timeout, and
/// the perpetual stream read must be exempt from the deadline.
/// </summary>
public class UsbTransferWatchdogTests
{
    // Far past anything the real clock reaches while a test runs, so only a fake clock can
    // fire it: a deadline armed on the system timer fails these tests instead of passing late.
    private static readonly TimeSpan Deadline = TimeSpan.FromHours(1);

    // Bounds a failure only. A correct transfer finishes as soon as the test acts.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static DeviceInfo TestInfo() => new()
    {
        Id = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test USB Device",
    };

    [Fact]
    public async Task Transfer_WedgedEndpoint_ThrowsUsbTimeoutException_WithTheDeadline()
    {
        var backend = new TestUsbBackend { BlockUntilCancelled = true };
        var time = new FakeTimeProvider();
        await using var dev = UsbDevice.CreateForTest(
            TestInfo(), backend, transferTimeout: Deadline, timeProvider: time);

        var write = dev.BulkWriteAsync(0x02, new byte[] { 1, 2, 3 });
        await backend.Blocked.WaitAsync(Bound);

        time.Advance(Deadline);

        var ex = await Assert.ThrowsAsync<UsbTimeoutException>(() => write.WaitAsync(Bound));
        Assert.Equal(Deadline, ex.Timeout);
    }

    [Fact]
    public async Task Transfer_CallerCancellation_SurfacesAsOperationCanceled_NotTimeout()
    {
        var backend = new TestUsbBackend { BlockUntilCancelled = true };
        // No deadline (infinite) — only the caller's token can end the transfer.
        await using var dev = UsbDevice.CreateForTest(TestInfo(), backend, transferTimeout: null);
        using var cts = new CancellationTokenSource();

        var write = dev.BulkWriteAsync(0x02, new byte[] { 1, 2, 3 }, cts.Token);
        await backend.Blocked.WaitAsync(Bound);
        cts.Cancel();

        // A UsbTimeoutException is NOT an OperationCanceledException, so this assertion
        // also proves caller cancellation is kept distinct from a watchdog timeout.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Bound));
    }

    [Fact]
    public async Task Transfer_WithinDeadline_CompletesNormally()
    {
        var backend = new TestUsbBackend(); // returns immediately
        await using var dev = UsbDevice.CreateForTest(
            TestInfo(), backend, transferTimeout: TimeSpan.FromSeconds(5));

        int written = await dev.BulkWriteAsync(0x02, new byte[] { 1, 2, 3, 4 });

        Assert.Equal(4, written);
    }

    [Fact]
    public async Task StreamRead_IsExemptFromTheDeadline()
    {
        // A pin-report endpoint legitimately blocks until data arrives, so the watchdog
        // must NOT apply to ReadBulkStreamAsync even with a deadline configured. Asserted as
        // the decision itself: which transfers asked the clock for a deadline.
        var backend = new TestUsbBackend();
        var clock = new TimerRecordingTimeProvider();
        await using var dev = UsbDevice.CreateForTest(
            TestInfo(), backend, transferTimeout: Deadline, timeProvider: clock);

        // Positive control: an ordinary transfer on this device does arm its deadline on the
        // injected clock, so an empty record below cannot mean the clock was never consulted.
        await dev.BulkWriteAsync(0x02, new byte[] { 1, 2, 3 });
        Assert.Equal(new[] { Deadline }, clock.RequestedDueTimes);

        backend.BlockUntilCancelled = true;
        using var cts = new CancellationTokenSource();
        var stream = Task.Run(async () =>
        {
            int produced = 0;
            await foreach (var _ in dev.ReadBulkStreamAsync(0x81, 41, cts.Token))
                produced++;
            return produced;
        });

        // Parked in the backend, so UsbDevice has already decided whether this read gets a
        // deadline: the write's is still the only one.
        await backend.Blocked.WaitAsync(Bound);
        Assert.Equal(new[] { Deadline }, clock.RequestedDueTimes);

        cts.Cancel();
        Assert.Equal(0, await stream.WaitAsync(Bound));
    }
}
