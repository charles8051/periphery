using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Periphery.Firmware;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Slice-3 mode-awareness (ADR-0063 DEC-004): the reducer's DeviceMode + Entering/WaitingForBootloader
/// stages, discovery widening to application-mode targets, and the format-conversion seam.
/// </summary>
public class ModeAwarenessTests
{
    // ── Reducer (pure) ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_application_mode_sets_mode_and_reboots_to_flash()
    {
        var s = AppReducer.Reduce(AppState.Empty,
            new AppEvent.TargetDetected("a", "Treehopper", "Treehopper", IdentificationMode.Passive, DeviceMode.Application));

        var t = s.Find("a")!;
        Assert.Equal(DeviceMode.Application, t.Mode);
        Assert.True(t.RebootsToFlash);
        Assert.Equal("Treehopper", t.ProviderName);
    }

    [Fact]
    public void Detect_defaults_to_bootloader_mode()
    {
        var s = AppReducer.Reduce(AppState.Empty, new AppEvent.TargetDetected("a", "ST DFU", "STM32 USB DFU"));
        Assert.Equal(DeviceMode.Bootloader, s.Find("a")!.Mode);
        Assert.False(s.Find("a")!.RebootsToFlash);
    }

    [Fact]
    public void App_mode_lifecycle_folds_entering_then_waiting_then_flashed()
    {
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "Treehopper", "Treehopper", IdentificationMode.Passive, DeviceMode.Application),
            new AppEvent.EnteringBootloader("a"));
        Assert.Equal(FlashStage.Entering, s.Find("a")!.Stage);

        s = AppReducer.Reduce(s, new AppEvent.WaitingForBootloader("a"));
        Assert.Equal(FlashStage.WaitingForBootloader, s.Find("a")!.Stage);

        // Then the flash on the re-enumerated bootloader proceeds on the same row.
        s = AppReducer.ReduceAll(s,
            new AppEvent.FlashStarted("a"),
            new AppEvent.FlashProgressed("a", new FlashProgress(FlashPhase.Writing, 5, 10)),
            new AppEvent.FlashFinished("a", FlashResult.Ok(10, verified: false)));
        Assert.Equal(FlashStage.Flashed, s.Find("a")!.Stage);
        Assert.Equal(100, s.Find("a")!.Percent);
    }

    [Fact]
    public void EnteringBootloader_clears_a_prior_error()
    {
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "Treehopper", "Treehopper", IdentificationMode.Passive, DeviceMode.Application),
            new AppEvent.OperationFailed("a", "stale error"),
            new AppEvent.EnteringBootloader("a"));

        Assert.Equal(FlashStage.Entering, s.Find("a")!.Stage);
        Assert.Null(s.Find("a")!.LastError);
    }

    // ── Discovery widening (service over fakes) ─────────────────────────────────

    [Fact]
    public async Task App_mode_device_is_detected_via_an_entry_as_an_application_target()
    {
        // No providers: the device is not a bootloader. An entry recognizes it as an app to reboot.
        var registry = new BootloaderRegistry();
        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper", d => d.Id == "app",
            new DeviceFilter().WithUsbId("10C4", "EAC9")));

        await using var svc = new FlashAnythingService(registry,
            FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("app", "Treehopper"), FakeDevices.Usb("other", "Mouse")),
            entries: entries);

        await svc.RefreshAsync();

        var t = Assert.Single(svc.State.Targets);
        Assert.Equal("app", t.Id);
        Assert.Equal(DeviceMode.Application, t.Mode);
        Assert.Equal("Treehopper", t.ProviderName);          // app-mode family = the entry's name
        Assert.Equal(IdentificationMode.Passive, t.Identification); // autoflash-eligible
    }

    [Fact]
    public async Task Bootloader_provider_wins_over_an_entry_when_both_match()
    {
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", _ => true, d => new FakeFirmwareProgrammer(d)));
        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper", _ => true, new DeviceFilter().WithUsbId("10C4", "EAC9")));

        await using var svc = new FlashAnythingService(registry,
            FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("dev")), entries: entries);
        await svc.RefreshAsync();

        Assert.Equal(DeviceMode.Bootloader, svc.State.Find("dev")!.Mode);
        Assert.Equal("EFM8", svc.State.Find("dev")!.ProviderName);
    }

    // ── Format conversion seam (service over fakes) ─────────────────────────────

    [Fact]
    public async Task A_loaded_format_is_converted_to_one_the_programmer_accepts()
    {
        var converted = FirmwarePayload.FromBlob(new byte[] { 1, 2, 3 }, FirmwareFormat.Efm8BootRecords);

        // The programmer accepts only boot records; a .hex loads as IntelHex, so it must be converted.
        var prog = new FakeFirmwareProgrammer(FakeDevices.Usb("efm8"),
            acceptedFormats: ImmutableArray.Create(FirmwareFormat.Efm8BootRecords));
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", _ => true, _ => prog));

        var converters = new FirmwareConverterRegistry();
        converters.Register(new FakeFirmwareConverter(FirmwareFormat.IntelHex, FirmwareFormat.Efm8BootRecords, _ => converted));

        await using var svc = new FlashAnythingService(registry,
            FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("efm8")), converters: converters);
        await svc.RefreshAsync();

        var hex = await TempHexAsync();
        try
        {
            await svc.LoadFirmwareAsync(hex);
            bool ok = await svc.FlashAsync("efm8");

            Assert.True(ok);
            Assert.NotNull(prog.FlashedPayload);
            Assert.Equal(FirmwareFormat.Efm8BootRecords, prog.FlashedPayload!.Format); // converted, not the raw IntelHex
            Assert.Equal(new byte[] { 1, 2, 3 }, prog.FlashedPayload!.Blob.ToArray());
        }
        finally { File.Delete(hex); }
    }

    [Fact]
    public async Task Flash_fails_clearly_when_no_converter_bridges_the_loaded_format()
    {
        var prog = new FakeFirmwareProgrammer(FakeDevices.Usb("efm8"),
            acceptedFormats: ImmutableArray.Create(FirmwareFormat.Efm8BootRecords));
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", _ => true, _ => prog));

        await using var svc = new FlashAnythingService(registry,
            FakeDevices.Watcher(new FakeMonitor(), FakeDevices.Usb("efm8"))); // no converters registered
        await svc.RefreshAsync();

        var hex = await TempHexAsync();
        try
        {
            await svc.LoadFirmwareAsync(hex);
            bool ok = await svc.FlashAsync("efm8");

            Assert.False(ok);
            Assert.Null(prog.FlashedPayload); // the gate refused before the programmer was handed anything
            Assert.Equal(FlashStage.Failed, svc.State.Find("efm8")!.Stage);
            Assert.Contains("no converter", svc.State.Find("efm8")!.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(hex); }
    }

    // ── App-mode flash end-to-end (service, riding the tracker — the slice-4 payoff) ────────────

    [Fact]
    public async Task App_mode_target_is_flashed_via_the_orchestration_riding_the_tracker()
    {
        var app = new DeviceInfo { Id = "app", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0x8A7E) };
        var boot = new DeviceInfo { Id = "boot", VendorId = new HardwareId(0x10C4), ProductId = new HardwareId(0xEAC9) };
        var monitor = new FakeMonitor();

        // The EFM8 bootloader programmer accepts boot records; we load a .efm8 so no conversion is needed.
        var prog = new FakeFirmwareProgrammer(boot, FlashResult.Ok(8, verified: false),
            acceptedFormats: ImmutableArray.Create(FirmwareFormat.Efm8BootRecords));
        var registry = new BootloaderRegistry();
        registry.Register(new FakeBootloaderProvider("EFM8", d => d.ProductId == new HardwareId(0xEAC9), _ => prog));

        // The entry recognizes the app and, on EnterAsync ("reboot"), plugs the bootloader.
        var entries = new BootloaderEntryRegistry();
        entries.Register(new FakeBootloaderEntry("Treehopper",
            d => d.ProductId == new HardwareId(0x8A7E),
            new DeviceFilter().WithUsbId("10C4", "EAC9"),
            onEnter: _ => { monitor.Plug(boot); return Task.CompletedTask; }));

        await using var svc = new FlashAnythingService(registry, FakeDevices.Watcher(monitor, app), entries: entries);
        await svc.RefreshAsync();
        Assert.Equal(DeviceMode.Application, svc.State.Find("app")!.Mode);

        var fw = await TempEfm8Async();
        try
        {
            await svc.LoadFirmwareAsync(fw);
            bool ok = await svc.FlashAsync("app");

            Assert.True(ok);
            Assert.Equal(FlashStage.Flashed, svc.State.Find("app")!.Stage);     // the row went app -> bootloader -> flashed
            Assert.Equal(FirmwareFormat.Efm8BootRecords, prog.FlashedPayload!.Format);
            // The re-enumerated bootloader was claimed by the in-flight flash, not surfaced separately.
            Assert.DoesNotContain(svc.State.Targets, t => t.Id == "boot");
        }
        finally { File.Delete(fw); }
    }

    // A minimal well-formed Intel HEX file (just the end-of-file record), so the loader accepts it.
    private static async Task<string> TempHexAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".hex");
        await File.WriteAllTextAsync(path, ":00000001FF\n");
        return path;
    }

    // A .efm8 boot-record file (content is opaque to the fake programmer, which just records it).
    private static async Task<string> TempEfm8Async()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".efm8");
        await File.WriteAllBytesAsync(path, new byte[] { (byte)'$', 0x03, 0x36, 0x00, 0x00 });
        return path;
    }
}
