using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// Tests for which PHYSICAL board <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/>
/// re-enters after a flash, when a sibling of the same model is on the bus the whole time
/// (periphery#173). The post-flash application wait accepts a pre-existing match, so a filter built
/// from VID/PID alone hands back whichever same-model board happens to be present — the verify round
/// then checks the wrong board's flash content (a false FAILED on a board that flashed correctly),
/// and a retry re-flashes it.
/// </summary>
public class BootloaderEntryOrchestratorVerificationSiblingTests
{
    private const ushort AppVid = 0x10C4, AppPid = 0x8A7E, BootVid = 0x10C4, BootPid = 0xEAC9;

    private static readonly DeviceInfo AppA = new()
    {
        Id = @"usb\vid_10c4&pid_8a7e\aaaaaaaa",
        VendorId = new HardwareId(AppVid), ProductId = new HardwareId(AppPid),
        SerialNumber = "AAAAAAAA", LocationPath = "PCIROOT(0)#USB(1)#USB(3)",
    };

    // A second Treehopper, on a different port, plugged in for the whole run and never touched.
    private static readonly DeviceInfo AppB = new()
    {
        Id = @"usb\vid_10c4&pid_8a7e\bbbbbbbb",
        VendorId = new HardwareId(AppVid), ProductId = new HardwareId(AppPid),
        SerialNumber = "BBBBBBBB", LocationPath = "PCIROOT(0)#USB(1)#USB(4)",
    };

    private static readonly DeviceInfo BootA = new()
    {
        Id = @"usb\vid_10c4&pid_eac9\6&aaaa&0&3",
        VendorId = new HardwareId(BootVid), ProductId = new HardwareId(BootPid),
        LocationPath = AppA.LocationPath,
    };

    // A fake bus with a persistent present-set, handing out one filtered view per wait — production
    // shape, unlike the single unfiltered source the other verification tests share. A sibling board
    // that never leaves the bus is exactly what the filtering is supposed to exclude, so a fake that
    // ignores the filter could not show the difference.
    private sealed class FakeBus
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, DeviceInfo> _present = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<View> _views = [];

        public FakeBus(params DeviceInfo[] initial)
        {
            foreach (var d in initial) _present[d.Id] = d;
        }

        public void Attach(DeviceInfo d)
        {
            List<View> views;
            lock (_gate) { _present[d.Id] = d; views = [.. _views]; }
            foreach (var v in views) v.Raise(d);
        }

        public void Detach(DeviceInfo d)
        {
            List<View> views;
            lock (_gate) { _present.Remove(d.Id); views = [.. _views]; }
            foreach (var v in views) v.RaiseGone(d);
        }

        public IDeviceWaitSource Open(DeviceFilter filter)
        {
            var view = new View(this, filter);
            lock (_gate) _views.Add(view);
            return view;
        }

        internal List<DeviceInfo> Snapshot() { lock (_gate) return [.. _present.Values]; }
        internal void Close(View view) { lock (_gate) _views.Remove(view); }

        internal sealed class View(FakeBus bus, DeviceFilter filter) : IDeviceWaitSource
        {
            public event Action<DeviceInfo>? Appeared;
            public event Action<string>? Disappeared;

            public Task StartAsync(CancellationToken ct)
            {
                foreach (var d in bus.Snapshot()) Raise(d);
                return Task.CompletedTask;
            }

            public void Raise(DeviceInfo d) { if (filter.Matches(d)) Appeared?.Invoke(d); }
            public void RaiseGone(DeviceInfo d) { if (filter.Matches(d)) Disappeared?.Invoke(d.Id); }
            public ValueTask DisposeAsync() { bus.Close(this); return ValueTask.CompletedTask; }
        }
    }

    // Reboots whichever app device it is handed into ITS OWN bootloader — so aiming the run at the
    // wrong board is visible as the wrong board being rebooted, not silently absorbed.
    private sealed class FakeEntry(FakeBus bus) : IBootloaderEntry
    {
        public string Name => "Fake";
        public bool CanEnter(DeviceInfo d) => d.VendorId == AppA.VendorId && d.ProductId == AppA.ProductId;
        public DeviceFilter ExpectedBootloader { get; } = new DeviceFilter().WithUsbId("10C4", "EAC9");

        public Task EnterAsync(DeviceInfo d, CancellationToken ct)
        {
            Assert.Equal(AppA.Id, d.Id); // the run is responsible for AppA and nothing else
            bus.Detach(d);
            bus.Attach(BootA);
            return Task.CompletedTask;
        }
    }

    private static BootloaderEntryOptions Options() => new()
    {
        // ApplicationFilter deliberately omitted: FlashAnythingService leaves it null, so the
        // derived filter is what decides which board the verify round re-enters.
        BootloaderTimeout = TimeSpan.FromMilliseconds(200),
        ApplicationTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task SiblingOnTheBus_VerifiesAgainstTheFlashedBoard()
    {
        var bus = new FakeBus(AppA, AppB);
        var entry = new FakeEntry(bus);
        var verified = new List<DeviceInfo>();

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, AppA,
            flash: (dev, ct) => { bus.Detach(BootA); bus.Attach(AppA); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verified.Add(dev); bus.Detach(BootA); bus.Attach(AppA); return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: Options(),
            waitSource: bus.Open);

        Assert.True(result.Verified);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(BootA.Id, Assert.Single(verified).Id);
    }

    [Fact]
    public async Task SiblingOnTheBus_AndTheFlashedBoardIsSlowToReturn_DoesNotAdoptTheSibling()
    {
        // AppA never comes back inside the window. The only same-model device present is AppB, which
        // this run has no claim on: adopting it would re-enter and verify (and on a mismatch,
        // re-flash) a board nobody asked to touch.
        var bus = new FakeBus(AppA, AppB);
        var entry = new FakeEntry(bus);
        var verified = new List<DeviceInfo>();

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, AppA,
            flash: (dev, ct) => { bus.Detach(BootA); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verified.Add(dev); return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: Options(),
            waitSource: bus.Open);

        Assert.Empty(verified);
        Assert.False(result.ApplicationReturned);
        Assert.False(result.Verified);
    }
}
