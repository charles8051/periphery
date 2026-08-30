using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Proves the flash shell actually flashes boards in parallel and honors the concurrency cap. The
/// fakes rendezvous inside FlashAsync so the assertions are deterministic: "both in flight at once"
/// only completes if two flashes genuinely overlap, and the cap test uses a single worker (which
/// physically cannot overlap) plus a peak-concurrency probe to show the bound holds.
/// </summary>
public class ParallelFlashTests
{
    private const string Family = "STM32 USB DFU";

    private static BootloaderRegistry Registry(Func<DeviceInfo, IFirmwareProgrammer> open)
    {
        var reg = new BootloaderRegistry();
        reg.Register(new FakeBootloaderProvider(Family, _ => true, open));
        return reg;
    }

    private static async Task<string> TempBinAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    // Autoflash flashes on background worker threads; await a condition on the State stream.
    private static async Task WaitUntil(FlashAnythingService svc, Func<AppState, bool> predicate, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource();
        void Handler(AppState s) { if (predicate(s)) tcs.TrySetResult(); }
        svc.StateChanged += Handler;
        try
        {
            if (predicate(svc.State)) return;
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        finally { svc.StateChanged -= Handler; }
    }

    [Fact]
    public async Task Two_boards_flash_concurrently_on_the_default_pool()
    {
        // The default ctor (no explicit cap) must ship parallel: two boards flash at the same time.
        int arrived = 0;
        var bothInFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Func<DeviceInfo, IFirmwareProgrammer> open = d => new GatedProgrammer(d, async ct =>
        {
            if (Interlocked.Increment(ref arrived) == 2) bothInFlight.TrySetResult();
            await release.Task.WaitAsync(ct); // hold both in flight until released
        });

        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(open), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            monitor.Plug(FakeDevices.Usb("a"));
            monitor.Plug(FakeDevices.Usb("b"));

            // Completes only if both boards are mid-flash at the same instant - impossible with a
            // one-at-a-time worker (the first would block here and the second would never start).
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();

            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 2);
            Assert.Equal(2, svc.State.AutoflashTally.Flashed);
        }
        finally { release.TrySetResult(); File.Delete(fw); } // unblock workers so disposal can't hang
    }

    [Fact]
    public async Task Respects_a_concurrency_cap_of_one()
    {
        // maxFlashConcurrency: 1 => a single worker => two flashes can never overlap.
        var probe = new ConcurrencyProbe();
        Func<DeviceInfo, IFirmwareProgrammer> open = d => new ProbeProgrammer(d, probe);

        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(open), FakeDevices.Watcher(monitor), maxFlashConcurrency: 1);
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            monitor.Plug(FakeDevices.Usb("a"));
            monitor.Plug(FakeDevices.Usb("b"));

            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 2);

            Assert.Equal(2, svc.State.AutoflashTally.Flashed); // both did flash...
            Assert.Equal(1, probe.Peak);                       // ...but never two at once
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task FlashAll_flashes_targets_concurrently()
    {
        // The CLI's `flash --all` routes through FlashAllAsync; prove it overlaps two boards.
        int arrived = 0;
        var bothInFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Func<DeviceInfo, IFirmwareProgrammer> open = d => new GatedProgrammer(d, async ct =>
        {
            if (Interlocked.Increment(ref arrived) == 2) bothInFlight.TrySetResult();
            await release.Task.WaitAsync(ct);
        });

        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(open),
            FakeDevices.Watcher(monitor, FakeDevices.Usb("a"), FakeDevices.Usb("b")));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            var flash = svc.FlashAllAsync();

            // Completes only if both boards are mid-flash at once - a sequential FlashAll would block.
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();

            var summary = await flash;
            Assert.Equal(2, summary.Succeeded);
        }
        finally { release.TrySetResult(); File.Delete(fw); }
    }

    // ── Per-family serialization of no-serial families (ADR-0063 DEC-005 / spec Safety rule 4) ──────
    //
    // Every EFM8 device in its bootloader enumerates as the shared id 0x10C4:0xEAC9 — no serial tells
    // two apart. Flashing two such boards concurrently through the app-mode reboot path corrupts on
    // real hardware (a garbage 0x90 reply): a physical current-collision on the shared USB bus, proven
    // by a stagger dose-response, NOT a software defect. So no-serial families (correlation =
    // FirstAppearance) must flash strictly one at a time; serial-bearing families (BySerial) stay
    // concurrent. These two tests pin both halves.

    private const string NoSerialFamily = "Treehopper";
    private static readonly HardwareId AppPid = new(0x8A7E);
    private static readonly HardwareId BootPid = new(0xEAC9);
    private static readonly HardwareId Vid = new(0x10C4);

    private static DeviceInfo App(string id, string? serial = null, string? location = null)
        => new() { Id = id, VendorId = Vid, ProductId = AppPid, SerialNumber = serial, LocationPath = location };

    // The bootloader an app re-enumerates as: shared VID/PID, carrying the app's serial (if any) so a
    // BySerial correlation can pick the right one, and the app's LocationPath (the physical USB port
    // is invariant across the reboot) so a ByLocationPath correlation can — even with no serial.
    private static DeviceInfo BootFor(DeviceInfo app)
        => new() { Id = app.Id + "-boot", VendorId = Vid, ProductId = BootPid, SerialNumber = app.SerialNumber, LocationPath = app.LocationPath };

    private static (BootloaderRegistry Registry, BootloaderEntryRegistry Entries) AppModeFakes(
        FakeMonitor monitor, ConcurrencyProbe probe)
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8",
            d => d.ProductId == BootPid, d => new ProbeProgrammer(d, probe)));

        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry(NoSerialFamily,
            d => d.ProductId == AppPid,
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            // "reboot" = the app re-enumerates as its bootloader.
            onEnter: app => { monitor.Plug(BootFor(app)); return Task.CompletedTask; }));
        return (registry, entries);
    }

    [Fact]
    public async Task No_serial_family_app_flashes_run_strictly_sequentially()
    {
        // Default entry options => FirstAppearance correlation (no-serial). Default pool (4) would run
        // both at once; the per-family gate must hold them to one-at-a-time regardless.
        var probe = new ConcurrencyProbe();
        var monitor = new FakeMonitor();
        var (registry, entries) = AppModeFakes(monitor, probe);

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor), entries: entries);
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(NoSerialFamily, FlashOptions.Default));

            monitor.Plug(App("appA"));
            monitor.Plug(App("appB"));

            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 2);

            Assert.Equal(2, svc.State.AutoflashTally.Flashed); // both boards did flash...
            Assert.Equal(1, probe.Peak);                       // ...but never two in flight at once
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Serial_bearing_family_app_flashes_still_overlap()
    {
        // BySerial correlation = a serial-bearing family: distinct serials survive the mode switch, so
        // two boards are individually addressable and safe to flash concurrently. The gate must NOT
        // apply here — the pool runs both at once.
        var probe = new ConcurrencyProbe();
        var monitor = new FakeMonitor();
        var (registry, entries) = AppModeFakes(monitor, probe);

        var bySerial = new BootloaderEntryOptions { Correlation = DeviceCorrelationMode.BySerial };
        await using var svc = new FlashAnythingService(
            registry, FakeDevices.Watcher(monitor), entries: entries, entryOptions: bySerial);
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(NoSerialFamily, FlashOptions.Default));

            monitor.Plug(App("appA", serial: "SN-A"));
            monitor.Plug(App("appB", serial: "SN-B"));

            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 2);

            Assert.Equal(2, svc.State.AutoflashTally.Flashed);
            Assert.Equal(2, probe.Peak); // serial families are not gated: both flashed at once
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Topology_correlated_no_serial_family_app_flashes_overlap()
    {
        // The concurrency unlock: a no-serial family (no serial survives the reboot) correlated by
        // ByLocationPath. Two boards on DISTINCT USB ports reboot and each correlates to its OWN
        // bootloader by port — so the per-family serialization gate must NOT apply and both flash at
        // once. This is the regression that proves topology correlation beats FirstAppearance, which
        // would (a) serialize the family and (b) collapse both waits onto the first-appearing bootloader.
        var probe = new ConcurrencyProbe();
        var monitor = new FakeMonitor();
        var (registry, entries) = AppModeFakes(monitor, probe);

        var byLocation = new BootloaderEntryOptions { Correlation = DeviceCorrelationMode.ByLocationPath };
        await using var svc = new FlashAnythingService(
            registry, FakeDevices.Watcher(monitor), entries: entries, entryOptions: byLocation);
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(NoSerialFamily, FlashOptions.Default));

            // No serial on either board — only the port tells them apart.
            monitor.Plug(App("appA", location: "PCIROOT(20)#USB(6)#USB(3)"));
            monitor.Plug(App("appB", location: "PCIROOT(20)#USB(6)#USB(4)"));

            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 2);

            Assert.Equal(2, svc.State.AutoflashTally.Flashed); // both boards flashed...
            Assert.Equal(2, probe.Peak);                       // ...and genuinely at the same time (not serialized)
        }
        finally { File.Delete(fw); }
    }

    /// <summary>A programmer that runs a supplied rendezvous inside FlashAsync (to force/observe overlap).</summary>
    private sealed class GatedProgrammer(DeviceInfo device, Func<CancellationToken, Task> onFlash) : IFirmwareProgrammer
    {
        public DeviceInfo Device { get; } = device;
        public ImmutableArray<FirmwareFormat> AcceptedFormats { get; } = ImmutableArray.Create(FirmwareFormat.RawBinary);
        public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default) => Task.FromResult(DeviceIdentity.Unknown("Fake"));

        public async Task<FlashResult> FlashAsync(
            FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
        {
            await onFlash(ct).ConfigureAwait(false);
            return FlashResult.Ok(payload.ByteLength, verified: true);
        }

        public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A programmer that records the high-water mark of simultaneously-active flashes.</summary>
    private sealed class ProbeProgrammer(DeviceInfo device, ConcurrencyProbe probe) : IFirmwareProgrammer
    {
        public DeviceInfo Device { get; } = device;
        public ImmutableArray<FirmwareFormat> AcceptedFormats { get; } = ImmutableArray.Create(FirmwareFormat.RawBinary);
        public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default) => Task.FromResult(DeviceIdentity.Unknown("Fake"));

        public async Task<FlashResult> FlashAsync(
            FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
        {
            probe.Enter();
            try { await Task.Delay(50, ct).ConfigureAwait(false); } // hold the slot long enough that a second worker would overlap
            finally { probe.Leave(); }
            return FlashResult.Ok(payload.ByteLength, verified: true);
        }

        public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Tracks the peak number of flashes that were active at the same time.</summary>
    private sealed class ConcurrencyProbe
    {
        private readonly object _lock = new();
        private int _current;
        private int _peak;
        public int Peak { get { lock (_lock) return _peak; } }
        public void Enter() { lock (_lock) { _current++; if (_current > _peak) _peak = _current; } }
        public void Leave() { lock (_lock) _current--; }
    }
}
