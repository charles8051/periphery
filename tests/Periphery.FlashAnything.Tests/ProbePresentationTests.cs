using System.Collections.Immutable;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// How a probe target is named to an operator (spec.md, "Presenting probe targets"). A fixture is a
/// position on a bench, not a device that identified itself.
/// </summary>
public class ProbePresentationTests
{
    private static FlashTargetView Probe(string port = "COM7", DeviceIdentity? identity = null) => new(
        Id: new DeviceId(@"USB\VID_10C4&PID_EA60\92EA014C0EF9EF11B9FC6E135C2A50C9"),
        DisplayName: "Silicon Labs CP210x USB to UART Bridge",
        ProviderName: "STM32 UART (AN3155)",
        Identification: IdentificationMode.Probe,
        Identity: identity,
        PortName: new SerialPortName(port));

    [Fact]
    public void A_probe_row_reads_as_its_fixture_not_its_usb_instance_id()
    {
        // The instance id is unreadable, and it names the wrong thing: what gets flashed is
        // whatever board happens to be in that fixture.
        Assert.Equal("COM7 (fixture)", Probe().OperatorLabel);
    }

    [Fact]
    public void Once_probed_the_row_carries_the_chip_that_answered()
    {
        var identity = new DeviceIdentity(
            Family: "STM32", Chip: "0x468", BootloaderVersion: "3.1", TransferSize: 256,
            Regions: ImmutableArray<MemoryRegion>.Empty, SupportedCommands: ImmutableArray<string>.Empty);

        Assert.Equal("COM7 (fixture, 0x468)", Probe(identity: identity).OperatorLabel);
    }

    [Fact]
    public void A_passive_row_keeps_its_own_name()
    {
        // It really did say what it is, so naming it for a port would be the wrong claim.
        var dfu = new FlashTargetView(
            new DeviceId("USB-DFU"), "STM32 BOOTLOADER", "STM32 USB DFU", IdentificationMode.Passive);

        Assert.Equal("STM32 BOOTLOADER", dfu.OperatorLabel);
    }

    [Fact]
    public void The_audit_uses_the_operator_label_when_one_is_given()
    {
        var id = new DeviceId(@"USB\VID_10C4&PID_EA60\92EA014C");

        var tally = AutoflashTally.Empty
            .With(AutoflashOutcomeKind.Flashed, id, null, "COM7 (fixture, 0x468)")
            .With(AutoflashOutcomeKind.Flashed, id, null, "COM7 (fixture, 0x468)");

        Assert.Equal("flashed COM7 (fixture, 0x468)", tally.Audit[0]);
        Assert.Equal("flashed COM7 (fixture, 0x468) #2", tally.Audit[1]);
    }

    [Fact]
    public void The_audit_falls_back_to_the_id_when_no_label_is_given()
    {
        var tally = AutoflashTally.Empty.With(AutoflashOutcomeKind.Flashed, new DeviceId("dfu-1"), null);

        Assert.Equal("flashed dfu-1", tally.Audit[0]);
    }
}
