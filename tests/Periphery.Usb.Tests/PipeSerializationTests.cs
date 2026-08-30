using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Usb.Tests.Fakes;

namespace Periphery.Usb.Tests;

/// <summary>
/// One transfer in flight per pipe (#263).
/// <para>
/// Neither WinUSB nor libusb serialises concurrent submissions on the same endpoint.
/// Before <see cref="UsbDevice"/> gated them, the invariant was a caller convention held
/// one layer above the resource — <c>TreehopperBoard._comsLock</c> — leaving every other
/// consumer (bootloaders, flasher, FrameFlow) with no protection at all.
/// </para>
/// </summary>
public class PipeSerializationTests
{
    private static DeviceInfo Info() => new()
    {
        Id   = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test USB device",
    };

    /// <summary>
    /// Records the maximum number of transfers concurrently inside the backend, per
    /// endpoint, and holds each one until released — so overlap is observed directly
    /// rather than inferred from timing.
    /// </summary>
    private sealed class OverlapProbe : IUsbBackend
    {
        private readonly ConcurrentDictionary<byte, int> _inFlight = new();
        private readonly ConcurrentDictionary<byte, int> _peak = new();
        private readonly TaskCompletionSource _release = new();

        public UsbDeviceDescriptor DeviceDescriptor { get; } = new()
        {
            UsbVersion = 0x0200,
            DeviceClass = 0xFF,
            MaxPacketSize0 = 64,
            VendorId = new HardwareId(0x10C4),
            ProductId = new HardwareId(0x8A7E),
        };

        public UsbConfigurationDescriptor Configuration { get; } = new()
        {
            ConfigurationValue = 1,
            MaxPowerMilliamps = 100,
        };

        /// <summary>Highest simultaneous occupancy seen on <paramref name="endpoint"/>.</summary>
        public int PeakFor(byte endpoint) => _peak.GetValueOrDefault(endpoint);

        /// <summary>Lets every parked transfer complete.</summary>
        public void ReleaseAll() => _release.TrySetResult();

        /// <summary>Signalled once <see cref="Entered"/> transfers have arrived.</summary>
        public TaskCompletionSource<int> ArrivedCount { get; } = new();

        public int ExpectArrivals { get; set; } = int.MaxValue;

        private int _entered;

        public int Entered => Volatile.Read(ref _entered);

        public void ClaimInterface(byte interfaceNumber) { }
        public void ReleaseInterface(byte interfaceNumber) { }

        public Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct)
            => EnterAsync(0, buffer.Length, ct);

        public Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct)
            => EnterAsync(endpointAddress, buffer.Length, ct);

        public Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct)
            => EnterAsync(endpointAddress, data.Length, ct);

        private async Task<int> EnterAsync(byte endpoint, int length, CancellationToken ct)
        {
            int now = _inFlight.AddOrUpdate(endpoint, 1, static (_, n) => n + 1);
            _peak.AddOrUpdate(endpoint, now, (_, p) => Math.Max(p, now));

            if (Interlocked.Increment(ref _entered) >= ExpectArrivals)
                ArrivedCount.TrySetResult(_entered);

            try
            {
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.AddOrUpdate(endpoint, 0, static (_, n) => n - 1);
            }

            return length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentWritesToOneEndpoint_NeverOverlapInTheBackend()
    {
        const int Callers = 10;

        using var meters = new MeterPeak("periphery.usb.queued_transfers");
        var probe = new OverlapProbe { ExpectArrivals = 1 };
        await using var device = UsbDevice.CreateForTest(Info(), probe);

        // Ten writers, one pipe. Without the gate every one of them reaches the backend
        // at once and peak occupancy is 10.
        var writers = Enumerable.Range(0, Callers)
            .Select(_ => device.BulkWriteAsync(0x02, new byte[8]))
            .ToArray();

        // One admitted, the rest parked on the gate. Waiting for that steady state is
        // what makes the assertion below meaningful: a sleep would pass just as happily
        // if the other nine had not been issued yet.
        await probe.ArrivedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await meters.WaitForAsync(
            "periphery.usb.queued_transfers", Callers - 1, TimeSpan.FromSeconds(5));

        Assert.Equal(1, probe.PeakFor(0x02));

        probe.ReleaseAll();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, probe.PeakFor(0x02));
    }

    [Fact]
    public async Task TransfersOnDifferentEndpoints_ProceedConcurrently()
    {
        // The gate must be per endpoint, not per device: a device-wide gate would let
        // ReadBulkStreamAsync's perpetual IN read block every write on the first open.
        var probe = new OverlapProbe { ExpectArrivals = 3 };
        await using var device = UsbDevice.CreateForTest(Info(), probe);

        var a = device.BulkWriteAsync(0x01, new byte[4]);
        var b = device.BulkWriteAsync(0x02, new byte[4]);
        var c = device.BulkReadAsync(0x81, 4);

        // All three must be inside the backend simultaneously. If the gate were
        // device-wide this wait never completes.
        int arrived = await probe.ArrivedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, arrived);

        probe.ReleaseAll();
        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, probe.PeakFor(0x01));
        Assert.Equal(1, probe.PeakFor(0x02));
        Assert.Equal(1, probe.PeakFor(0x81));
    }

    [Fact]
    public async Task ControlTransfersAreSerialisedToo_OnTheControlPipe()
    {
        const int Callers = 5;

        using var meters = new MeterPeak("periphery.usb.queued_transfers");
        var probe = new OverlapProbe { ExpectArrivals = 1 };
        await using var device = UsbDevice.CreateForTest(Info(), probe);

        var setup = new UsbControlSetup { RequestType = 0x40, Request = 0x52 };
        var calls = Enumerable.Range(0, Callers)
            .Select(_ => device.ControlTransferAsync(setup, new byte[2]))
            .ToArray();

        await probe.ArrivedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await meters.WaitForAsync(
            "periphery.usb.queued_transfers", Callers - 1, TimeSpan.FromSeconds(5));

        Assert.Equal(1, probe.PeakFor(0));

        probe.ReleaseAll();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AQueuedTransferStillHonoursItsDeadline_RatherThanWaitingForever()
    {
        // The deadline deliberately spans queueing as well as the wire. A caller queued
        // behind a transfer that is not going to finish must fault on its own deadline
        // rather than hang until the pipe frees.
        //
        // The holder is ReadBulkStreamAsync, whose per-transfer deadline is
        // Timeout.InfiniteTimeSpan BY DESIGN — an IN endpoint legitimately blocks until
        // the device sends. That is what makes this deterministic. An ordinary
        // BulkWriteAsync holder shares this device's 300 ms deadline and, having started
        // first, times out FIRST, releases the gate, and lets the queued caller through.
        // That is precisely how the first version of this test passed locally and failed
        // on CI: the assertion depended on which of two equal deadlines fired first.
        var probe = new OverlapProbe { ExpectArrivals = 1 };
        await using var device = UsbDevice.CreateForTest(
            Info(), probe, transferTimeout: TimeSpan.FromMilliseconds(300));

        using var streamCts = new CancellationTokenSource();
        var holder = Task.Run(async () =>
        {
            await foreach (var _ in device.ReadBulkStreamAsync(0x81, 8, streamCts.Token))
            {
            }
        });

        await probe.ArrivedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Queued behind the stream read on the same pipe: never reaches the backend.
        await Assert.ThrowsAsync<UsbTimeoutException>(() => device.BulkReadAsync(0x81, 8));

        Assert.Equal(1, probe.Entered);

        streamCts.Cancel();
        probe.ReleaseAll();

        // ReadBulkStreamAsync's contract is that cancellation ENDS the enumeration rather
        // than faulting it — see its `catch (OperationCanceledException) { yield break; }`
        // at UsbDevice.cs:170. So the holder completes cleanly, and asserting that is not
        // ceremony: a bare await passes whether the stream ends or faults, and would let a
        // change to that contract surface as a mystery cleanup failure instead of naming
        // itself here (#263 review turn 6).
        var outcome = await Record.ExceptionAsync(
            () => holder.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(outcome);
    }

    [Fact]
    public async Task QueuedCallersCountAsQueued_NotAsBackendInFlightWork()
    {
        // in_flight_transfers means "the backend is working on this". Counting a caller
        // parked on the gate would report ten in-flight transfers where the hardware has
        // one — false saturation on an existing instrument (#263 review turn 1).
        using var meters = new MeterPeak(
            "periphery.usb.in_flight_transfers", "periphery.usb.queued_transfers");

        const int Callers = 10;

        var probe = new OverlapProbe { ExpectArrivals = 1 };
        await using var device = UsbDevice.CreateForTest(Info(), probe);

        var writers = Enumerable.Range(0, Callers)
            .Select(_ => device.BulkWriteAsync(0x02, new byte[8]))
            .ToArray();

        await probe.ArrivedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Steady state, waited for rather than slept toward: one caller admitted to the
        // pipe, the other nine parked on the gate. Asserting a *peak* queue depth would
        // be interleaving-dependent — whether the admitted caller decrements before the
        // last one increments is a scheduling detail, so 9 and 10 are both legitimate
        // peaks and neither is worth pinning (#263 review turn 2/3).
        await meters.WaitForAsync(
            "periphery.usb.queued_transfers", Callers - 1, TimeSpan.FromSeconds(5));

        // This is the invariant with no such ambiguity: the gate admits exactly one, so
        // a queued caller counted as backend work would show a peak of Callers here.
        Assert.Equal(1, meters.Peak("periphery.usb.in_flight_transfers"));

        probe.ReleaseAll();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(10));

        // Both instruments must return to zero — no leak on either path.
        Assert.Equal(0, meters.Current("periphery.usb.in_flight_transfers"));
        Assert.Equal(0, meters.Current("periphery.usb.queued_transfers"));
    }

    [Fact]
    public async Task CallerCancellationReleasesThePipeForTheNextTransfer()
    {
        // A cancelled queued caller must not leave the gate held — the pipe has to be
        // usable by whoever is next in line.
        var probe = new OverlapProbe { ExpectArrivals = 1 };
        await using var device = UsbDevice.CreateForTest(Info(), probe);

        var holder = device.BulkWriteAsync(0x02, new byte[8]);
        await probe.ArrivedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var queued = device.BulkWriteAsync(0x02, new byte[8], cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);

        probe.ReleaseAll();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));

        // The gate is free: a fresh transfer completes rather than deadlocking.
        await device.BulkWriteAsync(0x02, new byte[8]).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
