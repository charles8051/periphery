using System;
using System.Threading;
using System.Threading.Tasks;
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
    private static DeviceInfo TestInfo() => new()
    {
        Id = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test USB Device",
    };

    [Fact]
    public async Task Transfer_WedgedEndpoint_ThrowsUsbTimeoutException_WithTheDeadline()
    {
        var backend = new TestUsbBackend { BlockUntilCancelled = true };
        await using var dev = UsbDevice.CreateForTest(
            TestInfo(), backend, transferTimeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<UsbTimeoutException>(
            () => dev.BulkWriteAsync(0x02, new byte[] { 1, 2, 3 }));

        Assert.Equal(TimeSpan.FromMilliseconds(100), ex.Timeout);
    }

    [Fact]
    public async Task Transfer_CallerCancellation_SurfacesAsOperationCanceled_NotTimeout()
    {
        var backend = new TestUsbBackend { BlockUntilCancelled = true };
        // No deadline (infinite) — only the caller's token can end the transfer.
        await using var dev = UsbDevice.CreateForTest(TestInfo(), backend, transferTimeout: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // A UsbTimeoutException is NOT an OperationCanceledException, so this assertion
        // also proves caller cancellation is kept distinct from a watchdog timeout.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dev.BulkWriteAsync(0x02, new byte[] { 1, 2, 3 }, cts.Token));
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
        // must NOT apply to ReadBulkStreamAsync even with a short deadline configured.
        var backend = new TestUsbBackend { BlockUntilCancelled = true };
        await using var dev = UsbDevice.CreateForTest(
            TestInfo(), backend, transferTimeout: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        int produced = 0;
        // Blocks ~300 ms (well past the 100 ms transfer deadline) then ends cleanly on
        // cancellation. If the deadline applied, this would throw UsbTimeoutException.
        await foreach (var _ in dev.ReadBulkStreamAsync(0x81, 41, cts.Token))
            produced++;

        Assert.Equal(0, produced);
    }
}
