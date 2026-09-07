using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Service-level autoflash: arm, simulate plug-ins through the fake watcher, and assert the
/// sequential flash + dedupe + passive-only gating + arm-time evaluation. The async shell (a
/// background flash worker) is awaited via the StateChanged stream.
/// </summary>
public class AutoflashServiceTests
{
    private const string Family = "STM32 USB DFU";

    private static BootloaderRegistry Registry(
        IdentificationMode mode = IdentificationMode.Passive, Action<string>? onOpen = null)
    {
        var reg = new BootloaderRegistry();
        reg.Register(new FakeBootloaderProvider(
            Family, _ => true,
            d => { onOpen?.Invoke(d.Id); return new FakeFirmwareProgrammer(d, FlashResult.Ok(64, verified: true)); },
            mode));
        return reg;
    }

    private static async Task<string> TempBinAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(path, new byte[64]);
        return path;
    }

    // Autoflash flashes on a background worker thread; await a condition on the State stream.
    private static async Task WaitUntil(FlashAnythingService svc, Func<AppState, bool> predicate, int timeoutMs = 3000)
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
    public async Task Arms_and_flashes_a_target_present_at_arm_time()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor, FakeDevices.ActiveUsb("dfu")));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));
            await WaitUntil(svc, s => s.AutoflashTally.Total >= 1);

            Assert.Equal(1, svc.State.AutoflashTally.Flashed);
            Assert.Equal(FlashStage.Flashed, svc.State.Find("dfu")!.Stage);
        }
        finally { File.Delete(fw); }
    }

    /// <summary>
    /// ADR-0088 D1: the open happens on activity, never on presence. A hot-plugged bootloader
    /// raises Appeared when its devnode enters the tree and Activated when its driver starts; on
    /// Windows those are two notifications with nothing serialising them. Opening on the first
    /// hands the provider a devnode with no driver, and the wasted attempt eats the recovery
    /// budget on healthy hardware. The target is still surfaced on presence (D2, inventory).
    /// </summary>
    [Fact]
    public async Task Does_not_flash_a_hotplugged_target_until_it_is_active()
    {
        var monitor = new FakeMonitor();
        var opens = new ConcurrentQueue<string>();
        await using var svc = new FlashAnythingService(Registry(onOpen: opens.Enqueue), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            monitor.Appear(FakeDevices.Usb("dfu"));
            await WaitUntil(svc, s => s.Find("dfu") is not null);
            await Task.Delay(200);
            Assert.Empty(opens);
            Assert.Equal(0, svc.State.AutoflashTally.Total);

            monitor.Activate(FakeDevices.Usb("dfu"));
            await WaitUntil(svc, s => s.AutoflashTally.Total >= 1);

            Assert.Equal(new[] { "dfu" }, opens.ToArray());
            Assert.Equal(FlashStage.Flashed, svc.State.Find("dfu")!.Stage);
        }
        finally { File.Delete(fw); }
    }

    /// <summary>
    /// The arm-time sweep has the same rule: a target that is present but not started when the
    /// bench is armed is not opened then; it is flashed on its first Active tick.
    /// </summary>
    [Fact]
    public async Task Does_not_flash_a_target_present_but_not_started_at_arm_time_until_it_starts()
    {
        var monitor = new FakeMonitor();
        var opens = new ConcurrentQueue<string>();
        await using var svc = new FlashAnythingService(Registry(onOpen: opens.Enqueue), FakeDevices.Watcher(monitor, FakeDevices.Usb("dfu")));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));
            await Task.Delay(200);
            Assert.Empty(opens);

            monitor.Activate(FakeDevices.Usb("dfu"));
            await WaitUntil(svc, s => s.AutoflashTally.Total >= 1);

            Assert.Equal(new[] { "dfu" }, opens.ToArray());
        }
        finally { File.Delete(fw); }
    }

    /// <summary>
    /// ADR-0088 D1, at the open rather than only at the enqueue. Autoflash is queued on the Active
    /// tick, but the open happens later on a worker; a `Deactivated` in that gap leaves the target
    /// present with a stale-active cached payload. The worker re-checks activity before the flash,
    /// so a target that is inactive at pickup is skipped rather than opened. (It is not un-marked,
    /// so like any skipped target it flashes on a later arm, not automatically — that keeps the
    /// skip free of a lost-update race with a concurrent reactivation.)
    /// </summary>
    [Fact]
    public async Task Skips_a_queued_target_that_goes_inactive_before_the_flash()
    {
        var monitor = new FakeMonitor();
        var opens = new ConcurrentQueue<string>();
        var releaseA = new TaskCompletionSource();
        var aOpening = new TaskCompletionSource();
        // maxFlashConcurrency 1: A holds the single worker while B waits in the queue, so the test
        // controls the gap between B's enqueue and B's flash deterministically.
        var reg = new BootloaderRegistry();
        reg.Register(new FakeBootloaderProvider(Family, _ => true,
            d =>
            {
                opens.Enqueue(d.Id);
                if (d.Id == "a") { aOpening.TrySetResult(); releaseA.Task.GetAwaiter().GetResult(); }
                return new FakeFirmwareProgrammer(d, FlashResult.Ok(64, verified: true));
            },
            IdentificationMode.Passive));
        await using var svc = new FlashAnythingService(reg, FakeDevices.Watcher(monitor), maxFlashConcurrency: 1);
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            monitor.Plug(FakeDevices.Usb("a"));
            await aOpening.Task.WaitAsync(TimeSpan.FromSeconds(5)); // worker is inside A's open

            monitor.Plug(FakeDevices.Usb("b"));                    // B enqueues behind A
            await WaitUntil(svc, s => s.Find("b") is not null);
            monitor.Deactivate(FakeDevices.Usb("b"));              // B's driver stops, B stays present

            releaseA.TrySetResult();                               // A finishes; worker turns to B and skips it
            await WaitUntil(svc, s => s.Find("a")!.Stage == FlashStage.Flashed);
            await Task.Delay(200);

            Assert.Equal(new[] { "a" }, opens.ToArray());          // B was never opened
            Assert.NotEqual(FlashStage.Flashed, svc.State.Find("b")!.Stage);
        }
        finally { releaseA.TrySetResult(); File.Delete(fw); }
    }

    [Fact]
    public async Task Flashes_a_hotplugged_target_while_armed()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor)); // empty snapshot
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            monitor.Plug(FakeDevices.Usb("dfu"));
            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 1);

            Assert.Equal(1, svc.State.AutoflashTally.Flashed);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Dedupes_a_re_enumerated_device()
    {
        var opens = 0;
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(
            Registry(onOpen: _ => Interlocked.Increment(ref opens)), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            var dfu = FakeDevices.Usb("dfu");
            monitor.Plug(dfu);
            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 1);
            monitor.Unplug(dfu); // the board resets out of the bootloader...
            monitor.Plug(dfu);   // ...then re-enumerates back through it
            await WaitUntil(svc, s => s.AutoflashTally.Skipped >= 1);

            Assert.Equal(1, svc.State.AutoflashTally.Flashed); // flashed exactly once
            Assert.Equal(1, opens);                            // opened/flashed exactly once
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Dedupes_a_re_enumerated_device_whose_id_case_flips()
    {
        // Windows re-enumerates the same USB device with different casing across a reset (an app device
        // \IMNUZ6YW returns as \imnuz6yw). A case-sensitive session dedupe treats the returned board as
        // new and flashes it a SECOND time (the autoflash double-flash). Device ids must compare
        // case-insensitively so the returned board is recognized as already flashed.
        var opens = 0;
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(
            Registry(onOpen: _ => Interlocked.Increment(ref opens)), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));

            monitor.Plug(FakeDevices.Usb("BoardA7DS6CD"));
            await WaitUntil(svc, s => s.AutoflashTally.Flashed >= 1);
            monitor.Unplug(FakeDevices.Usb("BoardA7DS6CD"));      // resets out of the bootloader...
            monitor.Plug(FakeDevices.Usb("boarda7ds6cd"));        // ...returns with flipped case
            await WaitUntil(svc, s => s.AutoflashTally.Skipped >= 1);

            Assert.Equal(1, svc.State.AutoflashTally.Flashed); // flashed exactly once despite the case flip
            Assert.Equal(1, opens);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Skips_a_probe_identified_target()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(IdentificationMode.Probe), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();
        var fw = await TempBinAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);

            // The guarantee moved earlier and got stronger. Arming a probe family without naming a
            // port used to produce a session that skipped every target it saw; it is now refused
            // outright, so there is no armed session for a probe target to be skipped by.
            await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default));
            Assert.Null(svc.State.Autoflash);
            Assert.Contains("without naming a port", svc.State.FirmwareError);

            monitor.Plug(FakeDevices.Usb("serial"));
            await Task.Delay(50);

            Assert.Equal(0, svc.State.AutoflashTally.Flashed);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task Does_nothing_when_disarmed()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        monitor.Plug(FakeDevices.Usb("dfu")); // detection is synchronous; no arm -> no autoflash

        Assert.Equal(0, svc.State.AutoflashTally.Total);
        Assert.NotEqual(FlashStage.Flashed, svc.State.Find("dfu")!.Stage);
    }

    [Fact]
    public async Task Arming_without_an_image_is_refused()
    {
        var monitor = new FakeMonitor();
        await using var svc = new FlashAnythingService(Registry(), FakeDevices.Watcher(monitor));
        await svc.RefreshAsync();

        await svc.DispatchAsync(new AppIntent.ArmAutoflash(Family, FlashOptions.Default)); // no image loaded

        Assert.Null(svc.State.Autoflash);        // not armed
        Assert.NotNull(svc.State.FirmwareError); // surfaced reason
    }
}
