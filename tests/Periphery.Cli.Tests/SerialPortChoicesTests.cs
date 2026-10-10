using Periphery.Cli.Commands;
using Periphery.Cli.Serial.Core;
using Spectre.Console.Testing;

namespace Periphery.Cli.Tests;

/// <summary>The rows <c>periphery serial select</c> offers, and the picker over them.</summary>
public sealed class SerialPortChoicesTests
{
    private static DeviceInfo Port(string id, string port, string? name, string? maker = null) => new()
    {
        Id = id, Name = name, Manufacturer = maker, PortName = new SerialPortName(port),
    };

    private static readonly DeviceInfo Com29 = Port(@"USB\JLINK", "COM29", "JLink CDC UART Port (COM29)", "SEGGER");
    private static readonly DeviceInfo Com4 = Port(@"FTDIBUS\A", "COM4", "USB Serial Port (COM4)", "FTDI");
    private static readonly DeviceInfo Com8 = Port(@"BTHENUM\A", "COM8", "Standard Serial over Bluetooth link (COM8)", "Microsoft");
    private static readonly DeviceInfo NoPort = new() { Id = @"ACPI\PNP0501", Name = "Communications Port" };

    [Fact]
    public void PortsAreOrderedByNumber_NotByText()
    {
        var choices = SerialPortChoices.From([Com29, Com8, Com4]);

        Assert.Equal(["COM4", "COM8", "COM29"], choices.Select(c => c.Device.PortName!.Value.Value));
    }

    [Fact]
    public void LinuxNodes_AreOrderedByNumberWithinEachStem()
    {
        var choices = SerialPortChoices.From([
            Port("a", "/dev/ttyUSB0", null), Port("b", "/dev/ttyS10", null),
            Port("c", "/dev/ttyS2", null), Port("d", "/dev/hvc0", null)]);

        Assert.Equal(
            ["/dev/hvc0", "/dev/ttyS2", "/dev/ttyS10", "/dev/ttyUSB0"],
            choices.Select(c => c.Device.PortName!.Value.Value));
    }

    [Fact]
    public void Label_AlignsPorts_DropsTheRepeatedPort_AndAddsTheMaker()
    {
        var choices = SerialPortChoices.From([Com29, Com4]);

        Assert.Equal(
            ["COM4   USB Serial Port (FTDI)", "COM29  JLink CDC UART Port (SEGGER)"],
            choices.Select(c => c.Label));
    }

    [Fact]
    public void Label_OmitsAMakerTheNameAlreadyStates_AndFallsBackToThePort()
    {
        var choices = SerialPortChoices.From([
            Port("a", "/dev/ttyUSB0", "FTDI FT232R", "FTDI"), Port("b", "/dev/ttyS0", null)]);

        Assert.Equal(["/dev/ttyS0", "/dev/ttyUSB0  FTDI FT232R"], choices.Select(c => c.Label));
    }

    [Fact]
    public void DevicesWithoutAPortName_AreNotOffered()
    {
        var choices = SerialPortChoices.From([NoPort, Com4]);

        Assert.Same(Com4, Assert.Single(choices).Device);
    }

    [Fact]
    public void NameFilter_KeepsMatchingNames_IgnoringCase()
    {
        var choices = SerialPortChoices.From([Com4, Com8, Com29], nameFilter: "usb serial");

        Assert.Same(Com4, Assert.Single(choices).Device);
    }

    [Fact]
    public async Task Picker_ArrowDownAndEnter_ChoosesTheSecondRow()
    {
        var console = new TestConsole().Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        var choices = SerialPortChoices.From([Com4, Com8, Com29]);

        var picked = await SerialSelectCommand.PromptAsync(console, choices, CancellationToken.None);

        Assert.Same(Com8, picked.Device);
        Assert.Contains("COM29  JLink CDC UART Port (SEGGER)", console.Output);
    }

    [Fact]
    public async Task Picker_ShowsALabelWithBrackets_Literally()
    {
        var bracketed = Port("x", "COM7", "Gadget [rev B] (COM7)");
        var console = new TestConsole().Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        var picked = await SerialSelectCommand.PromptAsync(
            console, SerialPortChoices.From([bracketed]), CancellationToken.None);

        Assert.Same(bracketed, picked.Device);
        Assert.Contains("COM7  Gadget [rev B]", console.Output);
    }
}
