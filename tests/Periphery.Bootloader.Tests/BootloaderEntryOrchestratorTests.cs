using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// Shell tests for the orchestration, now that its discovery is an injectable
/// <see cref="IDeviceWaitSource"/> (ADR-0063 slice 4): enter -> wait -> safety gate -> flash, the
/// reported phases, the gate, and the timeout — all driven by a fake source, no hardware.
/// </summary>
public class BootloaderEntryOrchestratorTests
{
    private static DeviceInfo Dev(string id, ushort vid, ushort pid) =>
        new() { Id = id, VendorId = new HardwareId(vid), ProductId = new HardwareId(pid) };

    private static readonly DeviceInfo App = Dev("app", 0x10C4, 0x8A7E);
    private static readonly DeviceInfo Boot = Dev("boot", 0x10C4, 0xEAC9);

    // A wait source the test drives: StartAsync replays a snapshot; the test fires later appearances.
    private sealed class FakeWaitSource(IEnumerable<DeviceInfo>? snapshot = null) : IDeviceWaitSource
    {
        public event Action<DeviceInfo>? Appeared;
        public event Action<string>? Disappeared;
        public Task StartAsync(CancellationToken ct)
        {
            if (snapshot is not null)
                foreach (var d in snapshot) Appeared?.Invoke(d);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Fire(DeviceInfo d) => Appeared?.Invoke(d);
    }

    private sealed class FakeEntry(
        string name, Func<DeviceInfo, bool> canEnter, DeviceFilter expected, Func<DeviceInfo, Task> onEnter) : IBootloaderEntry
    {
        public string Name => name;
        public bool CanEnter(DeviceInfo d) => canEnter(d);
        public DeviceFilter ExpectedBootloader => expected;
        public Task EnterAsync(DeviceInfo d, CancellationToken ct) => onEnter(d);
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    [Fact]
    public async Task RunAsync_enters_then_flashes_the_correlated_bootloader_reporting_phases()
    {
        var source = new FakeWaitSource();           // nothing present at arm
        var phases = new List<BootloaderEntryPhase>();
        DeviceInfo? flashed = null;

        // EnterAsync "reboots" the app: the bootloader appears as a fresh candidate.
        var entry = new FakeEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            _ => { source.Fire(Boot); return Task.CompletedTask; });

        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashed = dev; return Task.FromResult("ok"); },
            phase: new SyncProgress<BootloaderEntryPhase>(phases.Add),
            waitSource: _ => source);

        Assert.Equal("ok", result.FlashResult);
        Assert.False(result.ApplicationReturned);      // no application filter set
        Assert.Equal("boot", flashed!.Id);             // correlated + gated to the expected bootloader
        Assert.Equal(new[] { BootloaderEntryPhase.Entering, BootloaderEntryPhase.WaitingForBootloader }, phases);
    }

    [Fact]
    public async Task RunAsync_debounces_a_pre_existing_bootloader_and_takes_the_fresh_one()
    {
        // A bystander EFM8 bootloader is already on the bus (in the snapshot); only the one our reboot
        // produces should be flashed.
        var bystander = Dev("bystander", 0x10C4, 0xEAC9);
        var source = new FakeWaitSource(new[] { bystander });
        DeviceInfo? flashed = null;

        var entry = new FakeEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            _ => { source.Fire(Boot); return Task.CompletedTask; });

        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashed = dev; return Task.FromResult("ok"); },
            waitSource: _ => source);

        Assert.Equal("ok", result.FlashResult);
        Assert.Equal("boot", flashed!.Id);             // the bystander was debounced, not flashed
    }

    [Fact]
    public async Task RunAsync_safety_gate_refuses_a_device_that_is_not_the_expected_bootloader()
    {
        var source = new FakeWaitSource();
        bool flashCalled = false;

        // A buggy source surfaces a device that is NOT the expected bootloader; the gate must refuse.
        var wrong = Dev("wrong", 0x0483, 0xDF11); // an STM32 DFU, not the EFM8 bootloader
        var entry = new FakeEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            _ => { source.Fire(wrong); return Task.CompletedTask; });

        var ex = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => { flashCalled = true; return Task.FromResult("ok"); },
                waitSource: _ => source));

        Assert.Contains("Refusing to flash", ex.Message);
        Assert.False(flashCalled);
    }

    // ── ByLocationPath correlation — the USB port survives the reboot (parallel-safe) ───────────

    private const string PortA = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(3)";
    private const string PortB = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(4)";

    private static DeviceInfo DevAt(string id, ushort vid, ushort pid, string? location) =>
        new() { Id = id, VendorId = new HardwareId(vid), ProductId = new HardwareId(pid), LocationPath = location };

    [Fact]
    public async Task RunAsync_ByLocationPath_correlates_the_bootloader_on_the_apps_port_ignoring_another_port()
    {
        // The app is on PortA. When it reboots, a bootloader on PortA (ours) AND a bootloader on PortB
        // (another board rebooting concurrently) both appear — ByLocationPath must pick ours, and prove
        // no cross-correlation even though the other-port bootloader is fired first.
        var appOnA = DevAt("app", 0x10C4, 0x8A7E, PortA);
        var otherPortBoot = DevAt("boot-B", 0x10C4, 0xEAC9, PortB);
        var ourBoot = DevAt("boot-A", 0x10C4, 0xEAC9, PortA);

        var source = new FakeWaitSource();
        DeviceInfo? flashed = null;
        var entry = new FakeEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            _ => { source.Fire(otherPortBoot); source.Fire(ourBoot); return Task.CompletedTask; });

        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, appOnA,
            flash: (dev, ct) => { flashed = dev; return Task.FromResult("ok"); },
            options: new BootloaderEntryOptions { Correlation = DeviceCorrelationMode.ByLocationPath },
            waitSource: _ => source);

        Assert.Equal("ok", result.FlashResult);
        Assert.Equal("boot-A", flashed!.Id);   // correlated by port, NOT by first-appearance
    }

    [Fact]
    public async Task RunAsync_ByLocationPath_throws_when_the_app_device_has_no_location_path()
    {
        var appNoPort = DevAt("app", 0x10C4, 0x8A7E, location: null);
        var source = new FakeWaitSource();
        bool flashCalled = false;
        var entry = new FakeEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            _ => { source.Fire(Boot); return Task.CompletedTask; });

        var ex = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, appNoPort,
                flash: (dev, ct) => { flashCalled = true; return Task.FromResult("ok"); },
                options: new BootloaderEntryOptions { Correlation = DeviceCorrelationMode.ByLocationPath },
                waitSource: _ => source));

        Assert.Contains("LocationPath", ex.Message);
        Assert.False(flashCalled);
    }

    [Fact]
    public async Task RunAsync_times_out_when_the_bootloader_never_appears()
    {
        var source = new FakeWaitSource();           // nothing ever fires
        var entry = new FakeEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            _ => Task.CompletedTask);                // reboot, but no bootloader shows up

        var ex = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: new BootloaderEntryOptions { BootloaderTimeout = TimeSpan.FromMilliseconds(50) },
                waitSource: _ => source));

        Assert.Contains("did not re-enumerate", ex.Message);
    }
}
