using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Stm32.Usb.Tests;

/// <summary>
/// Shell tests: the GETSTATUS-driven flash loop over the fake transport — the AN3156
/// execution model, error reporting, and CLRSTATUS recovery. No hardware.
/// </summary>
public class Stm32DfuProgrammerTests
{
    private static DeviceInfo Device() => new() { Id = "stm32-dfu" };

    private static FakeStm32DfuTransport HappyFlashScript() =>
        new FakeStm32DfuTransport()
            .Ok(DfuState.DfuIdle)                                  // EnsureIdle
            .Ok(DfuState.DfuDnbusy).Ok(DfuState.DfuDnloadIdle)     // mass erase
            .Ok(DfuState.DfuDnbusy).Ok(DfuState.DfuDnloadIdle)     // set address
            .Ok(DfuState.DfuDnbusy).Ok(DfuState.DfuDnloadIdle)     // write block
            .Ok(DfuState.DfuManifest);                            // leave

    [Fact]
    public async Task FlashAsync_erases_sets_address_writes_and_leaves()
    {
        var fake = HappyFlashScript();
        var prog = Stm32DfuProgrammer.CreateForTest(Device(), fake, transferSize: 2048);

        var result = await prog.FlashAsync(FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x08000000, new byte[100]), FirmwareFormat.RawBinary), FlashOptions.Default with { Verify = false });

        Assert.True(result.Success);
        Assert.Equal(4, fake.Downloads.Count);
        Assert.Equal(new byte[] { 0x41 }, fake.Downloads[0].Data);                          // mass erase
        Assert.Equal(new byte[] { 0x21, 0x00, 0x00, 0x00, 0x08 }, fake.Downloads[1].Data);  // set address 0x08000000
        Assert.Equal((ushort)0, fake.Downloads[1].Block);
        Assert.Equal((ushort)2, fake.Downloads[2].Block);                                   // first data block
        Assert.Equal(100, fake.Downloads[2].Data.Length);
        Assert.Empty(fake.Downloads[3].Data);                                               // leave: zero-length DNLOAD
    }

    [Fact]
    public async Task FlashAsync_reports_progress_phases()
    {
        var fake = HappyFlashScript();
        var phases = new List<FlashPhase>();
        var prog = Stm32DfuProgrammer.CreateForTest(Device(), fake, transferSize: 2048);

        await prog.FlashAsync(
            FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x08000000, new byte[100]), FirmwareFormat.RawBinary), FlashOptions.Default with { Verify = false },
            new TestProgress(p => phases.Add(p.Phase)));

        Assert.Contains(FlashPhase.Erasing, phases);
        Assert.Contains(FlashPhase.Writing, phases);
        Assert.Contains(FlashPhase.Leaving, phases);
        Assert.Contains(FlashPhase.Done, phases);
    }

    [Fact]
    public async Task FlashAsync_fails_on_errTarget()
    {
        var fake = new FakeStm32DfuTransport()
            .Ok(DfuState.DfuIdle)                                  // EnsureIdle
            .Status(DfuStatusCode.ErrTarget, DfuState.DfuError);   // mass erase rejected

        var prog = Stm32DfuProgrammer.CreateForTest(Device(), fake, transferSize: 2048);
        var result = await prog.FlashAsync(FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x08000000, new byte[100]), FirmwareFormat.RawBinary), FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("ErrTarget", result.Error);
    }

    [Fact]
    public async Task FlashAsync_recovers_from_error_state_via_clrstatus()
    {
        var fake = new FakeStm32DfuTransport()
            .Status(DfuStatusCode.ErrVendor, DfuState.DfuError)    // EnsureIdle sees an error...
            .Ok(DfuState.DfuIdle)                                  // ...clears it, then idle
            .Ok(DfuState.DfuDnbusy).Ok(DfuState.DfuDnloadIdle)     // mass erase
            .Ok(DfuState.DfuDnbusy).Ok(DfuState.DfuDnloadIdle)     // set address
            .Ok(DfuState.DfuDnbusy).Ok(DfuState.DfuDnloadIdle)     // write block
            .Ok(DfuState.DfuManifest);                            // leave

        var prog = Stm32DfuProgrammer.CreateForTest(Device(), fake, transferSize: 2048);
        var result = await prog.FlashAsync(FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x08000000, new byte[100]), FirmwareFormat.RawBinary), FlashOptions.Default with { Verify = false });

        Assert.True(result.Success);
        Assert.Equal(1, fake.ClearStatusCalls);
    }

    [Fact]
    public async Task FlashAsync_reads_back_and_reports_verified_when_enabled()
    {
        // The fake's UPLOAD returns the image bytes, so the read-back matches.
        var fake = new FakeStm32DfuTransport { UploadResponse = new byte[100] };
        var prog = Stm32DfuProgrammer.CreateForTest(Device(), fake, transferSize: 2048);

        var result = await prog.FlashAsync(FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x08000000, new byte[100]), FirmwareFormat.RawBinary), FlashOptions.Default);

        Assert.True(result.Success);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task FlashAsync_fails_when_read_back_mismatches()
    {
        var corrupt = new byte[100];
        corrupt[0] = 0xFF;                                   // flash reads back a different first byte
        var fake = new FakeStm32DfuTransport { UploadResponse = corrupt };
        var prog = Stm32DfuProgrammer.CreateForTest(Device(), fake, transferSize: 2048);

        var result = await prog.FlashAsync(FirmwarePayload.FromImage(FirmwareImage.FromBytes(0x08000000, new byte[100]), FirmwareFormat.RawBinary), FlashOptions.Default);

        Assert.False(result.Success);
        Assert.Contains("verify", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestProgress(Action<FlashProgress> onReport) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => onReport(value);
    }
}
