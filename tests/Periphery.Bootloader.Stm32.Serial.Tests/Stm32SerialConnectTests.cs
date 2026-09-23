namespace Periphery.Bootloader.Stm32.Serial.Tests;

/// <summary>
/// <see cref="Stm32SerialProgrammer.ConnectAsync"/> — the handshake over a transport the caller
/// owns. <see cref="Stm32SerialProgrammer.OpenAsync"/> opens a port and owns it, which is wrong
/// for a caller that must hold the port across several operations: a probe loop keeps its handle
/// from the cycle that detects a target through the flash that follows.
/// </summary>
public class Stm32SerialConnectTests
{
    private static readonly DeviceInfo Device = new()
    {
        Id = "stm32-uart",
        PortName = new SerialPortName("COM7"),
    };

    private static readonly Stm32SerialOptions Quick = Stm32SerialOptions.Default with
    {
        SyncTimeout = TimeSpan.FromMilliseconds(250),
        CommandTimeout = TimeSpan.FromMilliseconds(250),
        SyncSettle = TimeSpan.FromMilliseconds(50),
        SyncSettleBudget = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public Task Connect_completes_the_handshake_on_a_fresh_part() => Simulation.RunAsync(async time =>
    {
        await using var device = new FakeStm32Bootloader(timeProvider: time) { ProductId = 0x0468 };

        await using var programmer = await Simulation.DriveAsync(time, Stm32SerialProgrammer.ConnectAsync(Device, device, Quick with { TimeProvider = time }));

        Assert.Equal("0x468", (await Simulation.DriveAsync(time, programmer.IdentifyAsync())).Chip);
    });

    [Fact]
    public Task Connect_completes_the_handshake_on_an_already_synced_part() => Simulation.RunAsync(async time =>
    {
        // The state a probe loop finds on every cycle after the first.
        await using var device = new FakeStm32Bootloader(timeProvider: time) { StartSynced = true, ProductId = 0x0468 };

        await using var programmer = await Simulation.DriveAsync(time, Stm32SerialProgrammer.ConnectAsync(Device, device, Quick with { TimeProvider = time }));

        Assert.Equal("0x468", (await Simulation.DriveAsync(time, programmer.IdentifyAsync())).Chip);
    });

    [Fact]
    public Task Connect_leaves_the_callers_transport_open() => Simulation.RunAsync(async time =>
    {
        // Ownership is unchanged: whoever created the pipe still closes it. A probe loop reusing
        // its port for the next cycle depends on this.
        await using var device = new FakeStm32Bootloader(timeProvider: time);

        await using (var programmer = await Simulation.DriveAsync(time, Stm32SerialProgrammer.ConnectAsync(Device, device, Quick with { TimeProvider = time })))
        {
            await Simulation.DriveAsync(time, programmer.IdentifyAsync());
        }

        // The programmer is disposed; the transport is not, so a second connect still works.
        await using var again = await Simulation.DriveAsync(time, Stm32SerialProgrammer.ConnectAsync(Device, device, Quick with { TimeProvider = time }));
        Assert.Equal("3.1", (await Simulation.DriveAsync(time, again.IdentifyAsync())).BootloaderVersion);
    });

    [Fact]
    public Task Connect_does_not_hand_back_a_programmer_when_the_handshake_fails() => Simulation.RunAsync(async time =>
    {
        // A dead line must fail the factory, not return something that looks usable.
        var pipe = new SilentPipe();

        await Assert.ThrowsAsync<Stm32SerialException>(
            () => Simulation.DriveAsync(time, Stm32SerialProgrammer.ConnectAsync(Device, pipe, Quick with { TimeProvider = time })));
    });

    private sealed class SilentPipe : System.IO.Pipelines.IDuplexPipe
    {
        private readonly System.IO.Pipelines.Pipe _in = new(Simulation.InlinePipes);
        private readonly System.IO.Pipelines.Pipe _out = new(Simulation.InlinePipes);
        public System.IO.Pipelines.PipeReader Input => _in.Reader;
        public System.IO.Pipelines.PipeWriter Output => _out.Writer;
    }
}
