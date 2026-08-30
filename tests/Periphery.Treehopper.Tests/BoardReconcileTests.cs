using System;
using System.Linq;
using System.Threading.Tasks;
using Periphery.Treehopper.Tests.Fakes;
using Periphery.Treehopper.Wire;
using Periphery.Usb;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Exercises the board-level API against a fake USB backend, asserting that the
/// reconcile path produces byte-exact wire packets. (ADR-0052 DEC-001 / DEC-003.)
/// </summary>
public class BoardReconcileTests
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

    // ── LED ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetLed_On_WritesLedPacketToPeripheralEndpoint()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await board.SetLedAsync(true);

        Assert.True(Wrote(b, 0x02, 0x0E, 0x01));
    }

    [Fact]
    public async Task SetLed_Off_WritesZeroPayload()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await board.SetLedAsync(false);

        // LedOn unchanged (false → false) → plan emits nothing
        Assert.Empty(b.Writes);
    }

    [Fact]
    public async Task SetLed_TrueAndThenFalse_WritesToggle()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await board.SetLedAsync(true);
        await board.SetLedAsync(false);

        Assert.True(Wrote(b, 0x02, 0x0E, 0x01)); // on
        Assert.True(Wrote(b, 0x02, 0x0E, 0x00)); // off
    }

    [Fact]
    public async Task SetLed_AfterDispose_Throws()
    {
        var board = BoardOver(new FakeUsbBackend());
        await board.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => board.SetLedAsync(true));
    }

    // ── GPIO ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pin_Configure_WritesConfigurePin()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await using var _ = await board.Pins[3].ConfigureAsync(PinMode.PushPullOutput);

        Assert.True(Wrote(b, 0x01, 3, 2, 0, 0, 0, 0)); // MakePushPullOutput
    }

    [Fact]
    public async Task Pin_Write_WritesSetDigitalValue()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await using var handle = await board.Pins[3].ConfigureAsync(PinMode.PushPullOutput);
        await handle.WriteAsync(true);

        Assert.True(Wrote(b, 0x01, 3, 5, 1, 0, 0, 0)); // SetDigitalValue high
    }

    [Fact]
    public async Task Pin_Dispose_WritesReleaseToDigitalInput()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        var handle = await board.Pins[3].ConfigureAsync(PinMode.PushPullOutput);
        await handle.DisposeAsync();

        Assert.True(Wrote(b, 0x01, 3, 1, 0, 0, 0, 0)); // MakeDigitalInput
    }

    [Fact]
    public void Board_Exposes20Pins()
    {
        var board = BoardOver(new FakeUsbBackend());
        Assert.Equal(20, board.Pins.Count);
        Assert.Equal(7, board.Pins[7].Number);
    }

    // ── I²C ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UseI2c_WritesConfigEnableWithRateByte()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await using var _ = await board.UseI2cAsync(100);

        Assert.True(Wrote(b, 0x02, 0x04, 0x01, 243));
    }

    [Fact]
    public async Task I2c_Dispose_WritesConfigDisable()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        var i2c = await board.UseI2cAsync();
        await i2c.DisposeAsync();

        Assert.True(Wrote(b, 0x02, 0x04, 0x00, 0x00)); // disable
    }

    [Fact]
    public async Task I2c_SendReceive_FramesTransactionAndStripsStatusByte()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 0xAB, 0xCD }); // status=OK + 2 data bytes
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        var read = await i2c.SendReceiveAsync(0x50, new byte[] { 0x01 }, readLength: 2);

        Assert.Equal(new byte[] { 0xAB, 0xCD }, read);
        Assert.True(Wrote(b, 0x02, 0x06, 0x50, 0x01, 0x02, 0x01));
        // [I2cTransaction, addr=0x50, txLen=1, rxLen=2, payload=0x01]
    }

    [Fact]
    public async Task I2c_Ping_TrueOnAck_FalseOnNack()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF }); // ACK
        b.ReadResponses.Enqueue(new byte[] { 0x01 }); // NACK
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        Assert.True(await i2c.PingAsync(0x42));
        Assert.False(await i2c.PingAsync(0x43));
    }

    [Fact]
    public async Task I2c_SendReceive_ThrowsTypedExceptionOnBusError()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0x01 }); // NACK
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        var ex = await Assert.ThrowsAsync<TreehopperI2cException>(
            () => i2c.SendReceiveAsync(0x50, ReadOnlyMemory<byte>.Empty, 0));
        Assert.Equal(I2cTransferError.Nack, ex.Error);
        Assert.Equal((byte)0x50, ex.Address);
    }

    // ── SPI ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Spi_Transfer_FramesHeaderPlusPayloadAndReturnsMiso()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xAA, 0xBB });
        await using var board = BoardOver(b);
        await using var spi = await board.UseSpiAsync(6, SpiMode.Mode00);

        var miso = await spi.TransferAsync(new byte[] { 0x01, 0x02 });

        Assert.Equal(new byte[] { 0xAA, 0xBB }, miso);
        Assert.True(Wrote(b, 0x02, 0x05, 0x01));                             // SpiConfig enable
        Assert.True(Wrote(b, 0x02, 0x07, 0xFF, 0x00, 0x03, 0x00, 0x00, 0x02, 0x01, 0x02)); // transaction
    }

    // ── UART ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Uart_Send_FramesConfigAndTransmit()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0x00 }); // transmit ack
        await using var board = BoardOver(b);
        await using var uart = await board.UseUartAsync(9600);

        await uart.SendAsync(new byte[] { 0x41, 0x42 });

        Assert.True(Wrote(b, 0x02, 0x03, 0x01, 48, 0x01, 0x00)); // UartConfig standard 9600
        Assert.True(Wrote(b, 0x02, 0x08, 0x00, 0x02, 0x41, 0x42)); // UartTransmit
    }

    [Fact]
    public async Task Uart_Receive_ReadsCountFromTrailingByte()
    {
        var b = new FakeUsbBackend();
        var response = new byte[33];
        response[0]  = 0x48; // 'H'
        response[1]  = 0x69; // 'i'
        response[32] = 2;    // count
        b.ReadResponses.Enqueue(response);
        await using var board = BoardOver(b);
        await using var uart = await board.UseUartAsync();

        var rx = await uart.ReceiveAsync();

        Assert.Equal(new byte[] { 0x48, 0x69 }, rx);
    }

    // ── PWM ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UsePwm_SendsInitialConfigWithFrequencyAndModeZero()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await using var _ = await board.UsePwmAsync(PwmFrequency.Freq183Hz);

        Assert.True(Wrote(b, 0x02, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0));
        // [PwmConfig, enableMode=0, freq=1 (183Hz), duties all 0]
    }

    [Fact]
    public async Task Pwm_SetDutyCycle_FramesPacketAndEnablesCumulatively()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var pwm = await board.UsePwmAsync(PwmFrequency.Freq732Hz);

        await pwm.SetDutyCycleAsync(PwmChannel.Pwm1, 1.0);
        Assert.True(Wrote(b, 0x02, 0x02, 0x01, 0x00, 0xFF, 0xFF, 0, 0, 0, 0));
        // enableMode=1, freq=0, pin7=0xFFFF

        await pwm.SetDutyCycleAsync(PwmChannel.Pwm3, 0.5);
        // cumulative mode→3; pin7 still 0xFFFF, pin8=0, pin9=0x8000
        Assert.True(Wrote(b, 0x02, 0x02, 0x03, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x80));
    }

    // ── Reports stream (DEC-002) ───────────────────────────────────────

    [Fact]
    public async Task InjectReport_AppearsInReportsStream()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        var report = TreehopperWire.DecodeReport(MakeReport(pin: 5, high: 0x01, low: 0x00), 0);
        board.InjectReportForTest(report);

        BoardReport? received = null;
        await foreach (var r in board.Reports)
        {
            received = r;
            break;
        }
        Assert.NotNull(received);
        Assert.Equal(0x100, received.Pins[5].Adc);
        Assert.True(received.Pins[5].Digital);
    }

    [Fact]
    public void LastReport_NullBeforeAnyReport()
    {
        var board = BoardOver(new FakeUsbBackend());
        Assert.Null(board.LastReport);
    }

    [Fact]
    public void LastReport_UpdatedByInject()
    {
        var b = new FakeUsbBackend();
        var board = BoardOver(b);
        var report = TreehopperWire.DecodeReport(MakeReport(0, 0x00, 0x01), 1);

        board.InjectReportForTest(report);

        Assert.Equal(1, board.LastReport!.Pins[0].Adc);
    }

    private static byte[] MakeReport(int pin, byte high, byte low)
    {
        var r = new byte[41];
        r[0] = 0x01; // non-zero ID → valid
        r[1 + pin * 2] = high;
        r[2 + pin * 2] = low;
        return r;
    }
}
