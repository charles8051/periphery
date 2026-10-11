using Periphery;
using Periphery.Bootloader.Stm32.Serial;
using Periphery.Bootloader.Stm32.Serial.Tests;
using Periphery.Serial.Browser.Tests.Fakes;

namespace Periphery.Serial.Browser.Tests;

/// <summary>
/// The ADR's "Web Serial pipe" gate on a simulated part: <see cref="Stm32SerialProgrammer"/> syncs
/// and identifies an STM32 system bootloader through <see cref="BrowserSerialPort.Pipe"/>, with the
/// fake Web Serial port in between. A live part in a browser is the gate proper.
/// </summary>
public class Stm32OverBrowserSerialPortTests
{
    private static readonly DeviceInfo Device = new() { Id = "web-serial" };

    private static readonly Stm32SerialOptions Quick = Stm32SerialOptions.Default with
    {
        SyncTimeout = TimeSpan.FromMilliseconds(250),
        CommandTimeout = TimeSpan.FromMilliseconds(250),
        SyncSettle = TimeSpan.FromMilliseconds(50),
        SyncSettleBudget = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public Task A_bootloader_syncs_and_identifies_through_the_ports_pipe() => Simulation.RunAsync(async time =>
    {
        await using var part = new FakeStm32Bootloader(timeProvider: time) { ProductId = 0x0468 };
        var serial = new PipeWebSerialPort(part);
        await using var port = new BrowserSerialPort(serial, 0x10C4, 0xEA60);
        await port.OpenAsync(new SerialPortOpenOptions { BaudRate = 115200, Parity = SerialPortParity.Even });

        await using var programmer = await Simulation.DriveAsync(
            time, Stm32SerialProgrammer.ConnectAsync(Device, port.Pipe, Quick with { TimeProvider = time }));
        var identity = await Simulation.DriveAsync(time, programmer.IdentifyAsync());

        Assert.Equal("0x468", identity.Chip);
        Assert.Contains(serial.Log, l => l.StartsWith("write ", StringComparison.Ordinal));
    });
}
