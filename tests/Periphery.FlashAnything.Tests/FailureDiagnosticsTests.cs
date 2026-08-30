using System;
using System.IO;
using System.Threading.Tasks;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// What a failed flash tells the operator. Both of these were blind spots found while flashing a
/// remote Treehopper fleet over SSH: the failure line named only the outermost wrapper ("Treehopper
/// reconcile failed.") and the reboot wait was stuck at its 15s default, so "slow board" and "board
/// never rebooted" were indistinguishable.
/// </summary>
public class FailureDiagnosticsTests
{
    [Fact]
    public async Task A_failed_reboot_surfaces_the_inner_cause_not_just_the_wrapper()
    {
        var app = new DeviceInfo { Id = "app", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0x8A7E) };

        // Shaped like the real fault: TreehopperException("Treehopper reconcile failed.", UsbException(...)).
        var wrapped = new InvalidOperationException(
            "Treehopper reconcile failed.",
            new IOException("Access is denied. (0x80070005)"));

        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper",
            d => d.ProductId == new HardwareId(0x8A7E),
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            onEnter: _ => throw wrapped));

        await using var svc = new FlashAnythingService(
            new BootloaderRegistry(), FakeDevices.Watcher(new FakeMonitor(), app), entries: entries);
        await svc.RefreshAsync();

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            Assert.False(await svc.FlashAsync("app"));

            string error = svc.State.Find("app")!.LastError!;
            Assert.Contains("Treehopper reconcile failed.", error, StringComparison.Ordinal);
            Assert.Contains("IOException: Access is denied. (0x80070005)", error, StringComparison.Ordinal);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task A_failed_flash_surfaces_the_inner_cause_of_an_open_failure()
    {
        var boot = FakeDevices.Usb("efm8");
        var wrapped = new InvalidOperationException("Open failed.", new IOException("The device is not responding."));

        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", _ => true, _ => throw wrapped));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(new FakeMonitor(), boot));
        await svc.RefreshAsync();

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            Assert.False(await svc.FlashAsync("efm8"));

            string error = svc.State.Find("efm8")!.LastError!;
            Assert.Contains("Open failed.", error, StringComparison.Ordinal);
            Assert.Contains("IOException: The device is not responding.", error, StringComparison.Ordinal);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task The_configured_bootloader_timeout_bounds_the_reboot_wait()
    {
        var app = new DeviceInfo { Id = "app", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0x8A7E) };

        // The entry "reboots" the board but nothing ever re-enumerates, so the wait runs to its bound.
        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper",
            d => d.ProductId == new HardwareId(0x8A7E),
            new DeviceFilter().WithUsbId("10C4", "EAC9")));

        await using var svc = new FlashAnythingService(
            new BootloaderRegistry(), FakeDevices.Watcher(new FakeMonitor(), app), entries: entries,
            entryOptions: new BootloaderEntryOptions { BootloaderTimeout = TimeSpan.FromMilliseconds(200) });
        await svc.RefreshAsync();

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            Assert.False(await svc.FlashAsync("app"));

            // The bound the orchestrator reports is the one configured, not the 15s default — the
            // whole point of the flag (and the test would take 15s if the option were dropped).
            Assert.Contains("within 0.2s", svc.State.Find("app")!.LastError!, StringComparison.Ordinal);
        }
        finally { File.Delete(fw); }
    }

    private static async Task<string> TempEfm8Async()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".efm8");
        await File.WriteAllBytesAsync(path, new byte[] { (byte)'$', 0x03, 0x36, 0x00, 0x00 });
        return path;
    }
}
