using System.Collections.Immutable;

namespace Periphery.Usb.Browser.Tests;

/// <summary>Rules 1 to 4 and 10 of ADR-0092 D7, for a bulk or interrupt IN endpoint.</summary>
public class InEndpointTests
{
    private static readonly CallerId A = new(1);
    private static readonly CallerId B = new(2);
    private static readonly CallerId C = new(3);
    private static readonly TransferId T1 = new(1);
    private static readonly TransferId T2 = new(2);

    private static InEndpointState Fresh() => new(maxPacketSize: 64);

    private static byte[] Bytes(int count, int start = 0)
        => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    private static (InEndpointState, ImmutableArray<EndpointOutput>) Step(InEndpointState s, EndpointInput i)
        => InEndpoint.Step(s, i);

    private static EndpointInput Request(CallerId c, int length) => new EndpointInput.Requested(c, length);
    private static EndpointInput Cancel(CallerId c) => new EndpointInput.Cancelled(c);
    private static EndpointInput SettleOk(TransferId t, byte[] data) => new EndpointInput.Settled(t, TransferResult.Ok.Read(data));
    private static EndpointInput SettleFault(TransferId t, Exception e) => new EndpointInput.Settled(t, new TransferResult.Fault(e));

    private static byte[] CompletedBytes(ImmutableArray<EndpointOutput> outputs, CallerId caller)
    {
        var complete = Assert.IsType<EndpointOutput.Complete>(Assert.Single(outputs));
        Assert.Equal(caller, complete.Caller);
        Assert.Equal(complete.Result.Data.Length, complete.Result.Count);
        return complete.Result.Data.ToArray();
    }

    // ── Rule 1: lengths are rounded up to whole packets ─────────────────

    [Theory]
    [InlineData(1, 64)]
    [InlineData(10, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(128, 128)]
    [InlineData(0, 64)]
    public void A_transfer_asks_for_whole_packets(int requested, int issued)
    {
        var (_, outputs) = Step(Fresh(), Request(A, requested));

        var issue = Assert.IsType<EndpointOutput.Issue>(Assert.Single(outputs));
        Assert.Equal(new EndpointOutput.Issue(T1, A, issued), issue);
    }

    [Theory]
    [InlineData(33_554_431, 512, 33_554_432)]
    [InlineData(33_554_432, 512, 33_554_432)]
    [InlineData(33_554_433, 512, 33_554_432)]
    [InlineData(int.MaxValue, 512, 33_554_432)]
    [InlineData(int.MaxValue, 10, 33_554_430)]
    [InlineData(33_554_431, 10, 33_554_430)]
    public void A_read_that_would_round_past_the_limit_asks_for_the_last_packet_boundary_within_it(
        int requested, int maxPacketSize, int issued)
    {
        Assert.Equal(issued, InEndpoint.RoundUpToPacket(requested, maxPacketSize));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(1023)]
    public void Under_the_limit_a_transfer_never_asks_for_less_than_the_read(int maxPacketSize)
    {
        for (int length = 0; length <= 4 * maxPacketSize; length++)
        {
            int rounded = InEndpoint.RoundUpToPacket(length, maxPacketSize);
            Assert.True(rounded >= length);
            Assert.Equal(0, rounded % maxPacketSize);
            Assert.True(rounded - length < maxPacketSize || length == 0);
        }
    }

    [Fact]
    public void A_read_gets_the_bytes_its_transfer_returned()
    {
        var (s1, _) = Step(Fresh(), Request(A, 10));

        var (s2, outputs) = Step(s1, SettleOk(T1, Bytes(10)));

        Assert.Equal(Bytes(10), CompletedBytes(outputs, A));
        Assert.Null(s2.InFlight);
        Assert.True(s2.Held.IsEmpty);
    }

    [Fact]
    public void Transfer_ids_count_up()
    {
        var (s1, _) = Step(Fresh(), Request(A, 8));
        var (s2, _) = Step(s1, SettleOk(T1, Bytes(8)));

        var (_, outputs) = Step(s2, Request(B, 8));

        Assert.Equal(T2, Assert.IsType<EndpointOutput.Issue>(Assert.Single(outputs)).Transfer);
    }

    // ── Rule 3: surplus is held and drained first ──────────────────────

    [Fact]
    public void Bytes_beyond_the_callers_buffer_are_held_for_the_next_read()
    {
        var (s1, _) = Step(Fresh(), Request(A, 10));
        var (s2, first) = Step(s1, SettleOk(T1, Bytes(64)));

        var (s3, second) = Step(s2, Request(B, 100));

        Assert.Equal(Bytes(10), CompletedBytes(first, A));
        Assert.Equal(Bytes(54, start: 10), CompletedBytes(second, B));
        Assert.True(s3.Held.IsEmpty);
        Assert.Equal(1, s3.TransfersIssued);
    }

    [Fact]
    public void Held_bytes_drain_across_several_reads_before_a_transfer_is_issued()
    {
        var (s1, _) = Step(Fresh(), Request(A, 4));
        var (s2, _) = Step(s1, SettleOk(T1, Bytes(64)));

        var (s3, b) = Step(s2, Request(B, 20));
        var (s4, c) = Step(s3, Request(C, 100));
        var (_, d) = Step(s4, Request(new CallerId(4), 8));

        Assert.Equal(Bytes(20, start: 4), CompletedBytes(b, B));
        Assert.Equal(Bytes(40, start: 24), CompletedBytes(c, C));
        Assert.Equal(new EndpointOutput.Issue(T2, new CallerId(4), 64), Assert.Single(d));
    }

    // ── Rule 2: one transfer outstanding, taken over by the next read ──

    [Fact]
    public void A_cancelled_read_leaves_its_transfer_running()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));

        var (s2, outputs) = Step(s1, Cancel(A));

        Assert.Equal(new EndpointOutput.Cancel(A), Assert.Single(outputs));
        Assert.Equal(new InFlightIn(T1, 64, Owner: null), s2.InFlight);
    }

    [Fact]
    public void A_read_takes_over_an_abandoned_transfer_instead_of_issuing_another()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));
        var (s2, _) = Step(s1, Cancel(A));

        var (s3, takeover) = Step(s2, Request(B, 64));
        var (_, settled) = Step(s3, SettleOk(T1, Bytes(30)));

        Assert.Empty(takeover);
        Assert.Equal(Bytes(30), CompletedBytes(settled, B));
    }

    [Fact]
    public void A_smaller_reader_taking_over_leaves_the_surplus_held()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, Request(B, 8));
        var (s4, b) = Step(s3, SettleOk(T1, Bytes(64)));

        var (_, c) = Step(s4, Request(C, 64));

        Assert.Equal(Bytes(8), CompletedBytes(b, B));
        Assert.Equal(Bytes(56, start: 8), CompletedBytes(c, C));
    }

    [Fact]
    public void An_abandoned_transfer_that_completes_with_no_reader_holds_its_bytes()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, settled) = Step(s2, SettleOk(T1, Bytes(12)));

        var (s4, b) = Step(s3, Request(B, 64));

        Assert.Empty(settled);
        Assert.Equal(Bytes(12), CompletedBytes(b, B));
        Assert.Equal(1, s4.TransfersIssued);
    }

    // ── Rule 4: an abandoned fault is held for the next read ───────────

    [Fact]
    public void An_abandoned_transfer_that_faults_holds_the_fault_for_the_next_read()
    {
        var fault = new IOException("stall");
        var (s1, _) = Step(Fresh(), Request(A, 64));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, settled) = Step(s2, SettleFault(T1, fault));

        var (s4, b) = Step(s3, Request(B, 64));
        var (_, c) = Step(s4, Request(C, 64));

        Assert.Empty(settled);
        Assert.Equal(new EndpointOutput.Fail(B, fault), Assert.Single(b));
        Assert.Equal(new EndpointOutput.Issue(T2, C, 64), Assert.Single(c));
    }

    [Fact]
    public void A_fault_on_an_owned_transfer_fails_its_reader()
    {
        var fault = new IOException("babble");
        var (s1, _) = Step(Fresh(), Request(A, 64));

        var (s2, outputs) = Step(s1, SettleFault(T1, fault));

        Assert.Equal(new EndpointOutput.Fail(A, fault), Assert.Single(outputs));
        Assert.Null(s2.HeldFault);
    }

    // ── Inputs that do not apply ───────────────────────────────────────

    [Fact]
    public void A_second_reader_while_one_is_waiting_is_rejected()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));

        var (s2, outputs) = Step(s1, Request(B, 64));

        Assert.Equal(new EndpointOutput.RejectConcurrent(B), Assert.Single(outputs));
        Assert.Same(s1, s2);
    }

    [Fact]
    public void A_settle_for_another_transfer_is_ignored()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));

        var (s2, outputs) = Step(s1, SettleOk(T2, Bytes(4)));

        Assert.Empty(outputs);
        Assert.Same(s1, s2);
    }

    [Fact]
    public void Cancelling_a_caller_that_already_completed_does_nothing()
    {
        var (s1, _) = Step(Fresh(), Request(A, 4));
        var (s2, _) = Step(s1, SettleOk(T1, Bytes(4)));

        var (s3, outputs) = Step(s2, Cancel(A));

        Assert.Empty(outputs);
        Assert.Same(s2, s3);
    }

    // ── Rule 10: disposal ──────────────────────────────────────────────

    [Fact]
    public void Disposal_fails_the_waiting_reader_and_discards_held_bytes_and_faults()
    {
        var (s1, _) = Step(Fresh(), Request(A, 4));
        var (s2, _) = Step(s1, SettleOk(T1, Bytes(64)));
        var (s3, _) = Step(s2, Request(B, 64));
        var (s4, _) = Step(s3, Request(C, 64));

        var (s5, outputs) = Step(s4, EndpointInput.Disposed.Instance);

        Assert.Equal(new EndpointOutput.FailDisposed(C), Assert.Single(outputs));
        Assert.True(s5.Held.IsEmpty);
        Assert.Null(s5.HeldFault);
        Assert.Null(s5.InFlight);
    }

    [Fact]
    public void Disposal_discards_a_held_fault()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, SettleFault(T1, new IOException("stall")));

        var (s4, _) = Step(s3, EndpointInput.Disposed.Instance);

        Assert.Null(s4.HeldFault);
    }

    [Fact]
    public void After_disposal_requests_fail_and_settles_are_ignored()
    {
        var (s1, _) = Step(Fresh(), Request(A, 64));
        var (s2, _) = Step(s1, EndpointInput.Disposed.Instance);

        var (s3, request) = Step(s2, Request(B, 64));
        var (s4, settle) = Step(s3, SettleOk(T1, Bytes(4)));
        var (_, again) = Step(s4, EndpointInput.Disposed.Instance);

        Assert.Equal(new EndpointOutput.FailDisposed(B), Assert.Single(request));
        Assert.Empty(settle);
        Assert.Empty(again);
    }
}
