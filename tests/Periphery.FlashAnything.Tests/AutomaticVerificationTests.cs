using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Periphery.Firmware;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// FlashAnythingService's entry.CanVerify branch (periphery#246): the exact integration point where
/// Peanut Gallery review caught two real bugs — a persistent verification mismatch being reported as
/// a successful flash, and the verify round's own re-entry phase events clobbering the flash's
/// already-reported success message. BootloaderEntryOrchestratorVerificationTests covers the retry
/// logic itself directly; these exercise it through FlashAnythingService's own wiring, the layer the
/// bugs actually lived in.
/// </summary>
public class AutomaticVerificationTests
{
    private static readonly DeviceInfo App =
        new() { Id = "app", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0x8A7E) };
    private static readonly DeviceInfo Boot =
        new() { Id = "boot", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0xEAC9) };

    private static async Task<string> TempEfm8Async()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".efm8");
        await File.WriteAllBytesAsync(path, new byte[] { (byte)'$', 0x03, 0x36, 0x00, 0x00 });
        return path;
    }

    [Fact]
    public async Task CanVerify_ContentMatches_ReportsSuccessAndVerifiedTrue()
    {
        var monitor = new FakeMonitor();
        // The automatic verify path re-enters the bootloader a second time, so the fake must
        // simulate the full appear/disappear lifecycle each way (not just "plug" on top of an
        // already-tracked-present device) for the watcher to see each transition as fresh.
        var prog = new FakeFirmwareProgrammer(Boot, FlashResult.Ok(8, verified: false),
            acceptedFormats: ImmutableArray.Create(FirmwareFormat.Efm8BootRecords),
            onFlashed: () => { monitor.Unplug(Boot); monitor.Plug(App); }); // resets to app mode, as RunApp does

        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", d => d.ProductId == new HardwareId(0xEAC9), _ => prog));

        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper",
            d => d.ProductId == new HardwareId(0x8A7E),
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            onEnter: _ => { monitor.Unplug(App); monitor.Plug(Boot); return Task.CompletedTask; },
            canVerify: true,
            // Independent check: content matches. Still simulates the unconditional leave-transfer
            // (Efm8VerifyOperation always attempts one, win or lose) so the app-wait afterward has
            // something to correlate - same reasoning as onFlashed above.
            verify: (_, _) => { monitor.Unplug(Boot); monitor.Plug(App); return Task.FromResult(true); }));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor, App), entries: entries);
        await svc.RefreshAsync();

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            bool ok = await svc.FlashAsync("app");

            Assert.True(ok);
            var t = svc.State.Find("app")!;
            Assert.Equal(FlashStage.Flashed, t.Stage);
            Assert.Null(t.LastError);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task CanVerify_PersistentMismatch_ReportsFailureNotSuccess()
    {
        // periphery#246 (turn 1 finding): the write's own ack said OK, but every independent,
        // later-session check disagreed. Must be reported as a failed flash, not silently accepted.
        var monitor = new FakeMonitor();
        var prog = new FakeFirmwareProgrammer(Boot, FlashResult.Ok(8, verified: false),
            acceptedFormats: ImmutableArray.Create(FirmwareFormat.Efm8BootRecords),
            onFlashed: () => { monitor.Unplug(Boot); monitor.Plug(App); });

        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", d => d.ProductId == new HardwareId(0xEAC9), _ => prog));

        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper",
            d => d.ProductId == new HardwareId(0x8A7E),
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            onEnter: _ => { monitor.Unplug(App); monitor.Plug(Boot); return Task.CompletedTask; },
            canVerify: true,
            // Independent check: always mismatches, but (like the real Efm8VerifyOperation) still
            // unconditionally attempts the leave-transfer regardless of the content answer - so this
            // genuinely exhausts every retry attempt on a persistent mismatch, rather than stopping
            // early because the app never confirmed returning.
            verify: (_, _) => { monitor.Unplug(Boot); monitor.Plug(App); return Task.FromResult(false); }));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor, App), entries: entries);
        await svc.RefreshAsync();

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            bool ok = await svc.FlashAsync("app");

            Assert.False(ok);
            var t = svc.State.Find("app")!;
            Assert.Equal(FlashStage.Failed, t.Stage);
            // The stale "flashed N bytes" success message from the last individual write attempt
            // must be replaced by the true, downgraded outcome - not left showing success text next
            // to a Failed stage.
            Assert.NotNull(t.LastError);
            Assert.DoesNotContain("flashed", t.Message ?? "", System.StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(fw); }
    }

    [Fact]
    public async Task CanVerify_False_NeverInvokesVerifyAsync()
    {
        // A family that does not opt in (CanVerify defaults false, as every existing FakeBootloaderEntry
        // call site already exercises) must behave exactly as before this feature existed - no extra
        // reboot-into-bootloader round-trip, no VerifyAsync call at all.
        bool verifyCalled = false;
        var monitor = new FakeMonitor();
        var prog = new FakeFirmwareProgrammer(Boot, FlashResult.Ok(8, verified: false),
            acceptedFormats: ImmutableArray.Create(FirmwareFormat.Efm8BootRecords));

        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", d => d.ProductId == new HardwareId(0xEAC9), _ => prog));

        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper",
            d => d.ProductId == new HardwareId(0x8A7E),
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            onEnter: _ => { monitor.Plug(Boot); return Task.CompletedTask; },
            canVerify: false,
            verify: (_, _) => { verifyCalled = true; return Task.FromResult(true); }));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor, App), entries: entries);
        await svc.RefreshAsync();

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            bool ok = await svc.FlashAsync("app");

            Assert.True(ok);
            Assert.False(verifyCalled);
        }
        finally { File.Delete(fw); }
    }
}
