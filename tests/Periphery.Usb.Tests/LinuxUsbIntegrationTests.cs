using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Periphery;
using Periphery.Usb;
using Xunit;

namespace Periphery.Usb.Tests;

/// <summary>
/// Device-backed libusb tests. They run only on the Linux device rig
/// (the Linux device rig), where <c>PERIPHERY_LINUX_DEVICE_TESTS=1</c> and the
/// hypervisor attaches QEMU-emulated USB HID devices (0627:0001) to an xHCI
/// controller. On the rig, a missing device is a hard failure — never a skip.
/// </summary>
public class LinuxUsbIntegrationTests
{
    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1";

    private static async Task<UsbDevice> OpenQemuHidDeviceAsync(CancellationToken ct)
    {
        var candidates = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Usb)
            .ToListAsync();

        var qemu = candidates
            .Where(d => d.VendorId?.Value == 0x0627 && d.ProductId?.Value == 0x0001)
            .ToList();
        Assert.True(qemu.Count > 0,
            "no QEMU-emulated USB HID device (0627:0001) enumerated — "
            + "is the VM's -device usb-kbd args wiring present? "
            + $"Saw {candidates.Count} USB devices.");

        // The usb subsystem yields both the device and its interfaces; the
        // backend resolves either, so take the first that opens.
        foreach (var candidate in qemu)
        {
            try
            {
                return await UsbDevice.OpenAsync(candidate, ct);
            }
            catch (UsbException)
            {
                // Try the next enumeration shape.
            }
        }

        Assert.Fail("every QEMU USB candidate failed to open");
        throw new InvalidOperationException(); // Unreachable.
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QemuHid_OpensAndReadsDescriptors()
    {
        if (!Enabled) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var device = await OpenQemuHidDeviceAsync(cts.Token);

        Assert.Equal(0x0627, device.Descriptor.VendorId.Value);
        Assert.Equal(0x0001, device.Descriptor.ProductId.Value);
        Assert.True(device.Descriptor.MaxPacketSize0 > 0);
        Assert.True(device.Descriptor.ConfigurationCount >= 1);

        Assert.NotEmpty(device.Configuration.Interfaces);
        var iface = device.Configuration.Interfaces[0];
        Assert.Equal(3, iface.InterfaceClass); // HID class.
        Assert.Contains(iface.Endpoints, e =>
            e.TransferType == UsbTransferType.Interrupt
            && e.Direction == UsbTransferDirection.DeviceToHost);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QemuHid_ControlTransfer_GetDeviceDescriptor()
    {
        if (!Enabled) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var device = await OpenQemuHidDeviceAsync(cts.Token);

        // Standard GET_DESCRIPTOR(DEVICE): the canonical raw control transfer.
        var buffer = new byte[18];
        int transferred = await device.ControlTransferAsync(
            new UsbControlSetup
            {
                RequestType = 0x80, // Device-to-host | standard | device.
                Request = 0x06,     // GET_DESCRIPTOR
                Value = 0x0100,     // DEVICE descriptor, index 0.
                Index = 0,
            },
            buffer,
            cts.Token);

        Assert.Equal(18, transferred);
        Assert.Equal(0x12, buffer[0]); // bLength
        Assert.Equal(0x01, buffer[1]); // bDescriptorType == DEVICE
        Assert.Equal(0x0627, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QemuHid_InterruptRead_CancelsPromptly()
    {
        if (!Enabled) return;

        using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var device = await OpenQemuHidDeviceAsync(openCts.Token);

        var interruptIn = device.Configuration.Interfaces[0].Endpoints.First(e =>
            e.TransferType == UsbTransferType.Interrupt
            && e.Direction == UsbTransferDirection.DeviceToHost);

        // Nobody is typing on the emulated keyboard, so the interrupt IN
        // endpoint stays silent; the read must block until cancellation and
        // then wake via libusb_cancel_transfer.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.BulkReadAsync(interruptIn.EndpointAddress, interruptIn.MaxPacketSize, cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"cancellation took {sw.Elapsed} — libusb_cancel_transfer path is broken");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QemuHid_DisposeWithAnInterruptReadInFlight_UnwindsTheReadInsteadOfStrandingIt()
    {
        // #263 item 2, Linux half. DisposeAsync used to set _shutdown, interrupt the event
        // handler and join the pump WITHOUT cancelling anything — after which no
        // libusb_handle_events would ever run again, so the completion callback could not
        // fire and the awaiting read hung for the life of the process. It then called
        // libusb_close with that transfer still pending, which libusb documents as
        // undefined behaviour.
        //
        // The read below is the same silent interrupt IN endpoint the cancellation test
        // uses, and the device is opened with no transfer timeout, so nothing but disposal
        // can end it. That is the whole point: this asserts DISPOSAL unwinds it.
        if (!Enabled) return;

        // The precondition — an URB actually submitted to libusb — is RETRIED, not asserted.
        //
        // in_flight_transfers is incremented by UsbDevice a few statements before
        // libusb_submit_transfer, so waiting on it narrows the window without closing it.
        // (It narrows it a lot: once SubmitAsync enters _lifetimeGate, disposal blocks on
        // that same lock, so the only losing window is the handful of instructions before
        // the lock is taken.) Nothing observable is emitted after the submit returns, so
        // there is no barrier available that would close it outright — short of adding
        // production surface for a test's benefit.
        //
        // Failing when the barrier loses would make a rare scheduling accident a red CI run;
        // ignoring it would let the test pass without ever exercising the drain. Retrying the
        // setup does neither: an attempt that never got the URB submitted is discarded and
        // repeated, and only a run that genuinely raced a submitted transfer is asserted on
        // (#270 review turn 3).
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            var run = await TryDisposeWithAnInterruptReadInFlightAsync();
            if (run is not { } result)
            {
                Assert.True(attempt < attempts,
                    $"in {attempts} attempts the read was never submitted before disposal won "
                    + "the race — the barrier is not holding, so the drain was never exercised");
                continue;
            }

            // Dispose waits for the drain, so by the time it returns the transfer has been
            // cancelled, its callback has run, and libusb_free_transfer has happened. Without
            // the fix this read never completes at all and the wait inside times out.
            Assert.IsAssignableFrom<OperationCanceledException>(result.ReadOutcome);

            // Well inside QuiesceTimeout: a cancelled URB comes back in microseconds, so
            // anything near the bound means the drain fell through to its give-up path.
            Assert.True(result.DisposeElapsed < TimeSpan.FromSeconds(2),
                $"disposal took {result.DisposeElapsed} — the drain did not quiesce promptly");
            return;
        }
    }

    /// <summary>
    /// One attempt at the dispose-with-a-transfer-in-flight race. Returns <c>null</c> when the
    /// read never reached libusb before disposal closed registration — that attempt proved
    /// nothing and is retried, rather than being reported as a failure of the drain.
    /// </summary>
    private static async Task<(Exception? ReadOutcome, TimeSpan DisposeElapsed)?>
        TryDisposeWithAnInterruptReadInFlightAsync()
    {
        using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var device = await OpenQemuHidDeviceAsync(openCts.Token);

        var interruptIn = device.Configuration.Interfaces[0].Endpoints.First(e =>
            e.TransferType == UsbTransferType.Interrupt
            && e.Direction == UsbTransferDirection.DeviceToHost);

        using var meters = new MeterPeak("periphery.usb.in_flight_transfers");

        // Nobody is typing on the emulated keyboard, so this parks with the URB submitted
        // and libusb owning both the transfer struct and its buffer.
        var read = device.BulkReadAsync(
            interruptIn.EndpointAddress, interruptIn.MaxPacketSize, CancellationToken.None);

        await meters.WaitForAsync("periphery.usb.in_flight_transfers", 1, TimeSpan.FromSeconds(5));

        // A hard failure, not a retry: if the endpoint is talking, no amount of repeating
        // will give this test the silent parked read it needs.
        Assert.False(read.IsCompleted, "the interrupt read completed on its own — the "
            + "emulated keyboard is not silent, so this test cannot observe what it exists for");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        sw.Stop();

        var outcome = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(5)));

        // The barrier lost: the read was refused before it reached libusb, so this attempt
        // never had a submitted URB for disposal to drain.
        if (outcome is ObjectDisposedException)
            return null;

        return (outcome, sw.Elapsed);
    }
}
