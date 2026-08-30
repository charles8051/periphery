using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Tests.Fakes;
using Periphery.Usb;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Response-endpoint accounting (#263 items 3 and 4).
/// <para>
/// A Treehopper transaction writes a command to <c>ep 0x02</c> and reads its reply from
/// <c>ep 0x82</c>. The wire protocol carries no sequence or correlation field, so a reply
/// nobody consumed is indistinguishable from the next command's — which makes a timed-out
/// or cancelled transaction a source of <em>silent</em> corruption rather than a visible
/// failure: the following I²C read returns some other command's bytes and looks fine.
/// </para>
/// <para>
/// The same blindness applied within a single response: <c>ReadChunkedAsync</c> advanced its
/// counter by the bytes it <em>asked</em> for rather than the bytes it got, so a short read
/// was zero-padded up to the expected length and handed back as a complete reply.
/// </para>
/// </summary>
public class BoardResponseDesyncTests
{
    private static DeviceInfo Info() => new()
    {
        Id   = @"\\?\usb#vid_10c4&pid_8a7e#test#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "Test Treehopper",
    };

    private static TreehopperBoard BoardOver(FakeUsbBackend b)
        => TreehopperBoard.CreateForTest(Info(), UsbDevice.CreateForTest(Info(), b));

    private static UsbTimeoutException TimedOut() =>
        new("transfer timed out", "test", TimeSpan.FromSeconds(2));

    // ── #263 item 3 — a stranded reply taints the connection ───────────

    [Fact]
    public async Task ATimedOutTransaction_LatchesDesync_AndTheNextOneRefusesTheStaleReply()
    {
        // The reported shape. The command is already on the wire when the read gives up, so
        // the device may still queue its reply — and the next transaction would read those
        // bytes as its own answer.
        var b = new FakeUsbBackend { OnBulkRead = _ => throw TimedOut() };
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        await Assert.ThrowsAsync<TreehopperException>(
            () => i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 4));

        Assert.True(board.IsResponsePipeDesynced);

        // Now the stale reply lands, and a fresh transaction asks a different question. It
        // must refuse rather than answer with the previous command's bytes.
        b.OnBulkRead = null;
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 0xDE, 0xAD, 0xBE, 0xEF });

        await Assert.ThrowsAsync<TreehopperDesyncException>(
            () => i2c.SendReceiveAsync(0x40, new byte[] { 0x02 }, readLength: 4));
    }

    [Fact]
    public async Task ACancelledTransaction_AlsoLatchesDesync()
    {
        // Cancellation strands a reply exactly as a timeout does — the command went out
        // either way. This is why the latch lives in a finally and not in the catch that
        // handles UsbException: an OperationCanceledException never reaches that catch.
        var parked = new TaskCompletionSource();
        var b = new FakeUsbBackend
        {
            OnBulkRead = ct => { parked.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); },
        };
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        using var cts = new CancellationTokenSource();
        var pending = i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 4, cts.Token);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.True(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task AFaultedTransactionThatWasOwedNoReply_DoesNotLatchDesync()
    {
        // UpdateName is a transaction with ResponseLength 0. Nothing was ever going to
        // arrive on the response endpoint, so a failed write leaves it as empty as it was —
        // latching here would cost a connection for no reason.
        var b = new FakeUsbBackend { OnBulkWrite = _ => throw TimedOut() };
        await using var board = BoardOver(b);

        await Assert.ThrowsAsync<TreehopperException>(() => board.UpdateNameAsync("nope"));

        Assert.False(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task ConfigWritesKeepWorkingAfterADesync()
    {
        // Scoped deliberately. Reconciles neither read the response endpoint nor put
        // anything on it, and they are the detached ones (LED flush, soft-PWM tick) whose
        // exceptions have nobody to catch them — the #259 / #262 hazard. Failing them would
        // widen the blast radius without buying any safety.
        var b = new FakeUsbBackend { OnBulkRead = _ => throw TimedOut() };
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        await Assert.ThrowsAsync<TreehopperException>(
            () => i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 4));
        Assert.True(board.IsResponsePipeDesynced);

        b.Writes.Clear();
        await board.SetLedAsync(true);

        Assert.NotEmpty(b.Writes);
    }

    [Fact]
    public async Task AnInterruptedOneWireSearch_LatchesDesync()
    {
        // The search streams ROM packets until a terminator, so giving up part way through
        // leaves the rest of them queued — the same stranded-reply shape, several packets
        // deep.
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0 }); // one ROM, then nothing
        await using var board = BoardOver(b);
        await using var ow = await board.UseOneWireAsync();

        // The second read finds the queue empty and faults rather than inventing a
        // terminator out of zeros (#263 item 4).
        await Assert.ThrowsAsync<TreehopperException>(() => ow.SearchAsync());

        Assert.True(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task ATransactionThatFailsBeforeTheCommandIsDispatched_DoesNotLatchDesync()
    {
        // The board is disposed under an open lease, so the transaction throws at its
        // _disposed check — before Encode, before any write. The device never saw a command,
        // so no reply can be queued, and latching here would cost a connection for nothing
        // (#271 review turn 1).
        var b = new FakeUsbBackend();
        var board = BoardOver(b);
        var i2c = await board.UseI2cAsync();

        await board.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 4));

        Assert.False(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task AWriteThatFaultsAfterDispatchStartsDoesLatch_BecauseTheBytesMayHaveLanded()
    {
        // The conservative half of the same rule, pinned so it is not "fixed" later. Once a
        // packet is handed to the transport, a fault tells us nothing about whether the
        // device received it — a bulk write can deliver and then fault on the acknowledgement
        // path. An undelivered command costs a needless reconnect; a delivered one whose
        // reply we ignore costs silent corruption, so the tie breaks toward latching.
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        // Attached only now: acquiring the lease reconciles, and that is a write too.
        b.OnBulkWrite = _ => throw TimedOut();

        await Assert.ThrowsAsync<TreehopperException>(
            () => i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 4));

        Assert.True(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task AMultiPacketCommandCutShortBeforeItsFinalPacket_DoesNotLatchDesync()
    {
        // 134 bytes of command (4 header + 130 payload) is three 64-byte packets. Failing on
        // the first leaves the firmware holding a truncated prefix, which it waits on rather
        // than answers — so no reply can be stranded and the connection should survive
        // (#271 review turn 3).
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        int writes = 0;
        b.OnBulkWrite = _ =>
        {
            if (++writes == 1) throw TimedOut();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<TreehopperException>(
            () => i2c.SendReceiveAsync(0x40, new byte[130], readLength: 4));

        Assert.Equal(1, writes);   // it really did stop before the later packets
        Assert.False(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task AMultiPacketCommandThatFailsOnItsFinalPacket_DoesLatchDesync()
    {
        // The complement. Reaching the last packet means every earlier one landed, so the
        // device may now hold a complete command — indeterminate, and the tie breaks toward
        // latching.
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        int writes = 0;
        b.OnBulkWrite = _ =>
        {
            if (++writes == 3) throw TimedOut();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<TreehopperException>(
            () => i2c.SendReceiveAsync(0x40, new byte[130], readLength: 4));

        Assert.Equal(3, writes);
        Assert.True(board.IsResponsePipeDesynced);
    }

    [Fact]
    public async Task ACancellationLandingBeforeTheFinalPacketIsIssued_DoesNotLatchDesync()
    {
        // The narrow window turn 5 named. The token is cancelled while packets 1 and 2 are
        // done and packet 3 has not yet cleared the pipe gate, so the transport never begins
        // it and the device cannot hold a complete command. `dispatched` is driven by
        // UsbDevice's issue callback rather than by WriteChunkedAsync being about to call it,
        // so nothing latches.
        var b = new FakeUsbBackend();
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        using var cts = new CancellationTokenSource();
        int writes = 0;
        b.OnBulkWrite = _ =>
        {
            // Cancel as the second of three packets completes; the third then fails on the
            // gate wait, before it is ever issued.
            if (++writes == 2) cts.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => i2c.SendReceiveAsync(0x40, new byte[130], readLength: 4, cts.Token));

        Assert.Equal(2, writes);   // the third packet never reached the backend
        Assert.False(board.IsResponsePipeDesynced);
    }

    // ── #263 item 4 — short-read accounting ───────────────────────────

    [Fact]
    public async Task AResponseSplitAcrossShortReads_IsAssembledWhole_NotZeroPadded()
    {
        // 9 bytes owed (1 status + 8 data), delivered 4 then 5. The old accounting advanced
        // by the 9 it requested, exited after the first read, and returned
        // [0xFF,1,2,3,0,0,0,0,0] — five bytes of padding presented as device data.
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 1, 2, 3 });
        b.ReadResponses.Enqueue(new byte[] { 4, 5, 6, 7, 8 });
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        var data = await i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 8);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, data);
        Assert.False(board.IsResponsePipeDesynced);   // it completed; nothing was stranded
    }

    [Fact]
    public async Task AResponseThatStopsArriving_Faults_RatherThanPaddingWithZeros()
    {
        // Same shape, but the remainder never comes. A zero-length read cannot be advanced
        // past — doing so would spin forever — and it must not be mistaken for data.
        var b = new FakeUsbBackend();
        b.ReadResponses.Enqueue(new byte[] { 0xFF, 1, 2, 3 });
        await using var board = BoardOver(b);
        await using var i2c = await board.UseI2cAsync();

        var ex = await Assert.ThrowsAsync<TreehopperException>(
            () => i2c.SendReceiveAsync(0x40, new byte[] { 0x01 }, readLength: 8));

        Assert.Contains("5 of 9", ex.Message);
        Assert.True(board.IsResponsePipeDesynced);
    }
}
