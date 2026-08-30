using System;
using System.Linq;
using System.Threading.Tasks;
using Periphery.Treehopper.Tests.Fakes;
using Periphery.Usb;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Shell-level integration tests for the peripherals completed in the core-SDK
/// parity pass: soft-PWM, 1-Wire, SPI burst modes, EEPROM identity / reboot, and the
/// parallel interface. These exercise the board's reconcile + transaction plumbing
/// (which the pure <c>TreehopperWire</c> tests don't), driving a fake USB backend.
/// </summary>
public class BoardPeripheralTests
{
    private static DeviceInfo Info() => new()
    {
        Id   = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test Treehopper",
    };

    private static TreehopperBoard BoardOver(FakeUsbBackend b)
        => TreehopperBoard.CreateForTest(Info(), UsbDevice.CreateForTest(Info(), b));

    private static bool Wrote(FakeUsbBackend b, byte endpoint, params byte[] data)
        => b.Writes.Any(w => w.Endpoint == endpoint && w.Data.SequenceEqual(data));

    // ── Soft-PWM ───────────────────────────────────────────────────────

    [Fact]
    public async Task SoftPwm_Configure_WritesPushPullThenAggregatePacket()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var pwm = await board.Pins[5].ConfigureSoftPwmAsync(0.5);

        Assert.True(Wrote(b, 0x01, 5, 2, 0, 0, 0, 0));                                 // pin 5 → push-pull
        Assert.True(Wrote(b, 0x02, 0x09, 0x02, 0x00, 0x7F, 0xFF, 0x05, 0x80, 0x00));   // soft-PWM schedule
    }

    [Fact]
    public async Task SoftPwm_Dispose_ReleasesPinAndDisables()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        var pwm = await board.Pins[5].ConfigureSoftPwmAsync(0.5);
        b.Writes.Clear();

        await pwm.DisposeAsync();

        Assert.True(Wrote(b, 0x01, 5, 1, 0, 0, 0, 0)); // pin 5 → digital-input release
        Assert.True(Wrote(b, 0x02, 0x09, 0x00));       // soft-PWM disabled
    }

    // ── 1-Wire ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OneWire_Search_DecodesRomsUntilTerminator()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0 }); // one ROM
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 });    // terminator
        await using var board = BoardOver(b);
        await using var ow = await board.UseOneWireAsync();

        var roms = await ow.SearchAsync();

        Assert.Single(roms);
        Assert.Equal(0x0100000000000000UL, roms[0]);
        Assert.True(Wrote(b, 0x02, 0x03, 0x02)); // UART → 1-Wire config
        Assert.True(Wrote(b, 0x02, 0x08, 0x03)); // scan sub-command
    }

    [Fact]
    public async Task OneWire_Reset_ReturnsPresence()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0x01 }); // presence pulse
        await using var board = BoardOver(b);
        await using var ow = await board.UseOneWireAsync();

        Assert.True(await ow.ResetAsync());
        Assert.True(Wrote(b, 0x02, 0x08, 0x02)); // 1-Wire reset sub-command
    }

    // ── SPI burst modes ────────────────────────────────────────────────

    [Fact]
    public async Task Spi_WriteAsync_BurstTx_FramesHeaderAndPayload()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var spi = await board.UseSpiAsync(clockMhz: 6);

        await spi.WriteAsync(new byte[] { 0xAB });

        // [cmd, cs=0xFF, csMode=0, clk=3, mode=0, burst=1(Tx), len=1, 0xAB] — no read
        Assert.True(Wrote(b, 0x02, 0x07, 0xFF, 0x00, 0x03, 0x00, 0x01, 0x01, 0xAB));
    }

    [Fact]
    public async Task Spi_ReadAsync_BurstRx_ReturnsClockedInBytes()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0x11, 0x22 });
        await using var board = BoardOver(b);
        await using var spi = await board.UseSpiAsync(clockMhz: 6);

        var data = await spi.ReadAsync(2);

        Assert.Equal(new byte[] { 0x11, 0x22 }, data);
        // header only — burst=2(Rx), len=2, no MOSI payload
        Assert.True(Wrote(b, 0x02, 0x07, 0xFF, 0x00, 0x03, 0x00, 0x02, 0x02));
    }

    // ── EEPROM identity & reboot ───────────────────────────────────────

    [Fact]
    public async Task UpdateNameAsync_FramesEepromWrite()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await board.UpdateNameAsync("AB");
        Assert.True(Wrote(b, 0x02, 0x0B, 0x02, 0x41, 0x42));
    }

    [Fact]
    public async Task RebootAsync_SendsRebootOpcode()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await board.RebootAsync();
        Assert.True(Wrote(b, 0x02, 0x0C));
    }

    [Fact]
    public async Task RebootIntoBootloaderAsync_SendsEnterBootloaderOpcode()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await board.RebootIntoBootloaderAsync();
        Assert.True(Wrote(b, 0x02, 0x0D));
    }

    [Fact]
    public async Task Version_ReadsFromUsbDescriptor()
    {
        var b = new FakeUsbBackend(); // DeviceVersion defaults to 0
        await using var board = BoardOver(b);
        Assert.Equal(0, board.Version);
        Assert.Equal("0.00", board.VersionString);
    }

    // ── Parallel interface ─────────────────────────────────────────────

    [Fact]
    public async Task Parallel_ConfigureAndWriteCommand_FramesPackets()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var par = await board.UseParallelAsync(
            new[] { 8, 9, 10, 11 }, registerSelectPin: 3);

        await par.WriteCommandAsync(new uint[] { 0x38 });

        // config: [cmd, en=1, delay=0, width=4, rs=3, rw=0xFF, e=0xFF, 8, 9, 10, 11]
        Assert.True(Wrote(b, 0x02, 0x0F, 0x01, 0x00, 0x04, 0x03, 0xFF, 0xFF, 0x08, 0x09, 0x0A, 0x0B));
        // command write: [cmd, sub=0(WriteCommand), count=1, 0x38]
        Assert.True(Wrote(b, 0x02, 0x10, 0x00, 0x01, 0x38));
    }
}
