using System.IO;
using System.Threading.Tasks;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The orchestration patterns (imperative shell) driven against fake providers: discovery,
/// the per-target flash, and the FlashAll fan-out with per-target error isolation.
/// </summary>
public class FlashAnythingServiceTests
{
    private static async Task<string> TempFirmwareAsync(int bytes = 64)
    {
        // A .bin so the format-detecting loader accepts it (it rejects unknown extensions).
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task Refresh_detects_only_devices_a_provider_matches()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider(
            "STM32 USB DFU", d => d.Id == "dfu", d => new FakeFirmwareProgrammer(d)));

        await using var svc = new FlashAnythingService(registry,
            FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("dfu", "ST DFU"), FakeDevices.Usb("other", "Mouse")));

        await svc.RefreshAsync();

        Assert.Single(svc.State.Targets);
        Assert.Equal("dfu", svc.State.Targets[0].Id);
        Assert.Equal("STM32 USB DFU", svc.State.Targets[0].ProviderName);
    }

    [Fact]
    public async Task Refresh_removes_vanished_targets()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("Fake", _ => true, d => new FakeFirmwareProgrammer(d)));

        var monitor = new FakeMonitor();
        var b = FakeDevices.Usb("b");
        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor, FakeDevices.Usb("a"), b));

        await svc.RefreshAsync();
        Assert.Equal(2, svc.State.Targets.Length);

        monitor.Unplug(b); // 'b' unplugged -> Disappeared -> TargetRemoved

        Assert.Single(svc.State.Targets);
        Assert.Equal("a", svc.State.Targets[0].Id);
    }

    [Fact]
    public async Task FlashAll_isolates_a_failing_target_and_summarises()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("Fake", _ => true, d =>
            d.Id == "bad"
                ? new FakeFirmwareProgrammer(d, throwOnFlash: true)                 // wedges mid-flash
                : new FakeFirmwareProgrammer(d, FlashResult.Ok(64, verified: true))));

        await using var svc = new FlashAnythingService(registry,
            FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("good"), FakeDevices.Usb("bad")));

        await svc.RefreshAsync();
        var fw = await TempFirmwareAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            var summary = await svc.FlashAllAsync();

            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Succeeded);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(FlashStage.Flashed, svc.State.Find("good")!.Stage); // the failure didn't stop it
            Assert.Equal(FlashStage.Failed, svc.State.Find("bad")!.Stage);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task FlashAll_with_no_firmware_skips_all_without_starting()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("Fake", _ => true, d => new FakeFirmwareProgrammer(d)));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("a")));
        await svc.RefreshAsync();

        var summary = await svc.FlashAllAsync();

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Succeeded);
        Assert.NotEqual(FlashStage.Writing, svc.State.Find("a")!.Stage); // never started a flash
    }

    [Fact]
    public async Task Flash_one_drives_identify_then_leave_on_success()
    {
        FakeFirmwareProgrammer? programmer = null;
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("Fake", _ => true,
            d => programmer = new FakeFirmwareProgrammer(d, FlashResult.Ok(64, verified: true))));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("a")));
        await svc.RefreshAsync();
        var fw = await TempFirmwareAsync();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            await svc.DispatchAsync(new AppIntent.Flash("a"));

            Assert.True(programmer!.IdentifyCalled);
            Assert.True(programmer!.LeaveCalled);
            Assert.Equal(FlashStage.Flashed, svc.State.Find("a")!.Stage);
        }
        finally { File.Delete(fw); }
    }
}
