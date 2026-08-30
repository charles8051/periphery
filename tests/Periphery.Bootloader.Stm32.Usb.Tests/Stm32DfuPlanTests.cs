using System.Linq;

namespace Periphery.Bootloader.Stm32.Usb.Tests;

/// <summary>Pure planner tests: image -> the ordered DFU step sequence.</summary>
public class Stm32DfuPlanTests
{
    [Fact]
    public void Plan_is_mass_erase_then_set_address_then_chunks_then_leave()
    {
        var image = FirmwareImage.FromBytes(0x08000000, new byte[5000]); // 2048 + 2048 + 904
        var steps = Stm32DfuPlan.Plan(image, transferSize: 2048, FlashOptions.Default);

        Assert.IsType<DfuStep.MassErase>(steps[0]);
        var setAddress = Assert.IsType<DfuStep.SetAddress>(steps[1]);
        Assert.Equal(0x08000000u, setAddress.Address);

        var writes = steps.OfType<DfuStep.WriteBlock>().ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal((ushort)2, writes[0].BlockNum);   // blocks restart at 2 after SetAddress
        Assert.Equal((ushort)4, writes[2].BlockNum);
        Assert.Equal(2048, writes[0].Data.Length);
        Assert.Equal(904, writes[2].Data.Length);      // final short chunk

        Assert.IsType<DfuStep.Leave>(steps[^1]);
    }

    [Fact]
    public void Plan_emits_a_set_address_per_segment_at_its_own_address()
    {
        // A multi-region image (e.g. from Intel HEX): each segment is written where it belongs,
        // never collapsed onto one base (the trap fixed in ADR-0061 phase 0).
        var image = FirmwareImage.FromSegments(new[]
        {
            new FirmwareSegment(0x08000000, new byte[10]),
            new FirmwareSegment(0x08010000, new byte[10]),
        });

        var steps = Stm32DfuPlan.Plan(image, transferSize: 2048, FlashOptions.Default);

        var setAddresses = steps.OfType<DfuStep.SetAddress>().Select(s => s.Address).ToArray();
        Assert.Equal(new[] { 0x08000000u, 0x08010000u }, setAddresses);

        // Leave jumps to the lowest address (the application's entry point).
        var leave = Assert.IsType<DfuStep.Leave>(steps[^1]);
        Assert.Equal(0x08000000u, leave.JumpAddress);
    }

    [Fact]
    public void Plan_emits_a_read_back_verify_per_segment_after_writes_before_leave()
    {
        var image = FirmwareImage.FromSegments(new[]
        {
            new FirmwareSegment(0x08000000, new byte[10]),
            new FirmwareSegment(0x08010000, new byte[10]),
        });

        var steps = Stm32DfuPlan.Plan(image, transferSize: 2048, FlashOptions.Default); // Verify = true

        var verifies = steps.OfType<DfuStep.Verify>().ToArray();
        Assert.Equal(new[] { 0x08000000u, 0x08010000u }, verifies.Select(v => v.Address).ToArray());

        var list = steps.ToList();
        int lastWrite = list.FindLastIndex(s => s is DfuStep.WriteBlock);
        int firstVerify = list.FindIndex(s => s is DfuStep.Verify);
        int leave = list.FindIndex(s => s is DfuStep.Leave);
        Assert.True(lastWrite < firstVerify, "verify must run after all writes");
        Assert.True(firstVerify < leave, "verify must run before leave");

        // ...and is absent when verify is off.
        var noVerify = Stm32DfuPlan.Plan(image, 2048, FlashOptions.Default with { Verify = false });
        Assert.DoesNotContain(noVerify, s => s is DfuStep.Verify);
    }

    [Fact]
    public void Plan_skips_erase_and_leave_when_disabled()
    {
        var steps = Stm32DfuPlan.Plan(
            FirmwareImage.FromBytes(0x08000000, new byte[10]), 2048,
            FlashOptions.Default with { Erase = EraseMode.None, LeaveAfterFlash = false });

        Assert.DoesNotContain(steps, s => s is DfuStep.MassErase);
        Assert.DoesNotContain(steps, s => s is DfuStep.Leave);
    }
}
