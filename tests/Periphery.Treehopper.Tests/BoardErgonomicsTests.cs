using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Tests.Fakes;
using Periphery.Treehopper.Wire;
using Periphery.Usb;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Covers the ergonomic surface added in the API review: broadcast Reports,
/// per-pin reads / watch, the public declarative <see cref="TreehopperBoard.ReconcileAsync"/>,
/// and the I²C convenience wrappers.
/// </summary>
public class BoardErgonomicsTests
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

    private static byte[] RawReport(int pin, byte high, byte low)
    {
        var r = new byte[41];
        r[0] = 0x01; // non-zero ID → valid
        r[1 + pin * 2] = high;
        r[2 + pin * 2] = low;
        return r;
    }

    private static BoardReport Report(int pin, byte high, byte low)
        => TreehopperWire.DecodeReport(RawReport(pin, high, low), 0);

    // ── Broadcast Reports ──────────────────────────────────────────────

    [Fact]
    public async Task Reports_TwoIndependentSubscribers_BothSeeCurrentState()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        board.InjectReportForTest(Report(0, 0x01, 0x00)); // sets current state (0x100)

        // Each enumeration is an independent subscription. Under the old single-
        // consumer channel the second loop would have starved (the one item already
        // consumed); broadcast + seed-on-subscribe gives both the current state.
        async Task<int> FirstAdcAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var r in board.Reports.WithCancellation(cts.Token))
                return r.Pins[0].Adc;
            return -1;
        }

        Assert.Equal(0x100, await FirstAdcAsync());
        Assert.Equal(0x100, await FirstAdcAsync());
    }

    [Fact]
    public async Task Reports_LiveReport_ReachesAnAlreadySubscribedConsumer()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        // Drive the enumerator manually so the subscription is registered before
        // the report is published (no seed yet → the first MoveNext blocks).
        await using var e = board.Reports.GetAsyncEnumerator();
        var move = e.MoveNextAsync();               // registers the subscriber, then awaits
        board.InjectReportForTest(Report(0, 0x02, 0x00)); // 0x200

        Assert.True(await move);
        Assert.Equal(0x200, e.Current.Pins[0].Adc);
    }

    // ── Per-pin reads ──────────────────────────────────────────────────

    [Fact]
    public async Task PinHandle_ReadAsync_ReturnsCurrentSnapshot()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        board.InjectReportForTest(Report(3, 0x0A, 0xBC)); // 0x0ABC
        await using var pin = await board.Pins[3].ConfigureAsync(PinMode.AnalogInput);

        var snap = await pin.ReadAsync();

        Assert.Equal(0x0ABC, snap.Adc);
        Assert.Equal(0x0ABC, await pin.ReadAnalogAsync());
    }

    [Fact]
    public async Task PinHandle_ReadVoltageAsync_HonoursReference()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        board.InjectReportForTest(Report(0, 0x07, 0xFE)); // ADC 2046
        await using var pin = await board.Pins[0].ConfigureAsync(PinMode.AnalogInput);

        Assert.Equal(2046 / 4092.0 * 3.3, await pin.ReadVoltageAsync(),      precision: 4);
        Assert.Equal(2046 / 4092.0 * 1.65, await pin.ReadVoltageAsync(1.65), precision: 4);
    }

    [Fact]
    public async Task PinHandle_ReadDigitalAsync_TracksHighByte()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        board.InjectReportForTest(Report(5, 0x01, 0x00));
        await using var pin = await board.Pins[5].ConfigureAsync(PinMode.DigitalInput);

        Assert.True(await pin.ReadDigitalAsync());
    }

    // ── Per-pin watch (de-duplicated) ──────────────────────────────────

    [Fact]
    public async Task PinHandle_WatchAsync_DeduplicatesConsecutiveValues()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var pin = await board.Pins[0].ConfigureAsync(PinMode.AnalogInput);

        await using var e = pin.WatchAsync().GetAsyncEnumerator();

        var a = Report(0, 0x01, 0x00); // 0x100
        var c = Report(0, 0x02, 0x00); // 0x200

        var m1 = e.MoveNextAsync();        // registers subscriber, then blocks
        board.InjectReportForTest(a);
        Assert.True(await m1);
        Assert.Equal(0x100, e.Current.Adc);

        var m2 = e.MoveNextAsync();
        board.InjectReportForTest(a);      // duplicate — filtered, must not satisfy m2
        board.InjectReportForTest(c);      // distinct — satisfies m2
        Assert.True(await m2);
        Assert.Equal(0x200, e.Current.Adc);
    }

    // ── Declarative ReconcileAsync ─────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_AppliesMultipleChangesInOneCall()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        await board.ReconcileAsync(cfg => cfg with
        {
            LedOn = true,
            Pins  = cfg.Pins.SetItem(3, new PinConfig(PinMode.PushPullOutput, true)),
            I2c   = new I2cConfig(400),
        });

        Assert.True(Wrote(b, 0x02, 0x0E, 0x01));        // LED on
        Assert.True(Wrote(b, 0x01, 3, 2, 0, 0, 0, 0));  // pin 3 → push-pull output
        Assert.True(Wrote(b, 0x01, 3, 5, 1, 0, 0, 0));  // pin 3 → drive high
        Assert.True(Wrote(b, 0x02, 0x04, 0x01, 253));   // I²C enable @ 400 kHz (rate 253)
    }

    [Fact]
    public async Task ReconcileAsync_NullTransform_Throws()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await Assert.ThrowsAsync<ArgumentNullException>(() => board.ReconcileAsync(null!));
    }

    // ── I²C convenience wrappers ───────────────────────────────────────

    [Fact]
    public async Task I2c_Write_FramesWriteOnlyTransaction()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF }); // ack
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        await i2c.WriteAsync(0x50, new byte[] { 0xDE, 0xAD });

        Assert.True(Wrote(b, 0x02, 0x06, 0x50, 0x02, 0x00, 0xDE, 0xAD)); // txLen=2, rxLen=0
    }

    [Fact]
    public async Task I2c_Read_FramesReadOnlyTransaction()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 0x11, 0x22 }); // status + 2 data
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        var data = await i2c.ReadAsync(0x50, 2);

        Assert.Equal(new byte[] { 0x11, 0x22 }, data);
        Assert.True(Wrote(b, 0x02, 0x06, 0x50, 0x00, 0x02)); // txLen=0, rxLen=2
    }

    [Fact]
    public async Task I2c_WriteRead_FramesBothStages()
    {
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 0xAB });
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        var data = await i2c.WriteReadAsync(0x50, new byte[] { 0x10 }, 1);

        Assert.Equal(new byte[] { 0xAB }, data);
        Assert.True(Wrote(b, 0x02, 0x06, 0x50, 0x01, 0x01, 0x10)); // txLen=1, rxLen=1, reg 0x10
    }

    // ── Resync (open-loop state re-assert) ─────────────────────────────

    [Fact]
    public async Task ResyncAsync_ReissuesConfigureDeviceThenFullConfig()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);

        // Build up some committed state.
        await board.SetLedAsync(true);
        await using var i2c = await board.UseI2cAsync(100);
        b.Writes.Clear(); // ignore the setup writes; assert only what Resync re-sends

        await board.ResyncAsync();

        Assert.True(Wrote(b, 0x02, 0x01, 0x00));      // ConfigureDevice — full firmware reset
        Assert.True(Wrote(b, 0x02, 0x0E, 0x01));      // LED re-asserted on
        Assert.True(Wrote(b, 0x02, 0x04, 0x01, 243)); // I²C re-enabled @ 100 kHz (rate 243)
    }

    [Fact]
    public async Task ResyncAsync_AfterResync_NoOpReconcileEmitsNothing()
    {
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await board.SetLedAsync(true);

        await board.ResyncAsync();
        b.Writes.Clear();

        // _applied is restored, so an identity reconcile plans no commands.
        await board.ReconcileAsync(cfg => cfg);
        Assert.Empty(b.Writes);
    }

    [Fact]
    public async Task ResyncAsync_AfterDispose_Throws()
    {
        var board = BoardOver(new FakeUsbBackend());
        await board.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => board.ResyncAsync());
    }
}
