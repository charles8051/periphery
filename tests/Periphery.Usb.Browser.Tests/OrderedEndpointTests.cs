using System.Collections.Immutable;

namespace Periphery.Usb.Browser.Tests;

/// <summary>
/// Rules 5 to 10 of ADR-0092 D7, for a bulk or interrupt OUT endpoint and for control transfers.
/// </summary>
public class OrderedEndpointTests
{
    private static readonly CallerId A = new(1);
    private static readonly CallerId B = new(2);
    private static readonly CallerId C = new(3);
    private static readonly TransferId T1 = new(1);
    private static readonly TransferId T2 = new(2);

    private static (OrderedEndpointState, ImmutableArray<EndpointOutput>) Step(OrderedEndpointState s, EndpointInput i)
        => OrderedEndpoint.Step(s, i);

    private static EndpointInput Request(CallerId c, int length = 16) => new EndpointInput.Requested(c, length);
    private static EndpointInput Cancel(CallerId c) => new EndpointInput.Cancelled(c);
    private static EndpointInput Settle(TransferId t, TransferResult r) => new EndpointInput.Settled(t, r);

    [Fact]
    public void A_request_on_an_idle_endpoint_is_issued_at_its_length()
    {
        var (s1, outputs) = Step(OrderedEndpointState.Idle, Request(A, 300));

        Assert.Equal(new EndpointOutput.Issue(T1, A, 300), Assert.Single(outputs));
        Assert.Equal(new InFlightOrdered(T1, A), s1.InFlight);
    }

    [Fact]
    public void A_write_completes_with_the_count_written()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));

        var (s2, outputs) = Step(s1, Settle(T1, TransferResult.Ok.Written(16)));

        Assert.Equal(new EndpointOutput.Complete(A, TransferResult.Ok.Written(16)), Assert.Single(outputs));
        Assert.Null(s2.InFlight);
    }

    [Fact]
    public void A_control_read_completes_with_its_own_data()
    {
        var reply = TransferResult.Ok.Read(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00 });
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A, 6));

        var (_, outputs) = Step(s1, Settle(T1, reply));

        Assert.Same(reply, Assert.IsType<EndpointOutput.Complete>(Assert.Single(outputs)).Result);
    }

    [Fact]
    public void A_fault_fails_the_caller()
    {
        var fault = new IOException("stall");
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));

        var (_, outputs) = Step(s1, Settle(T1, new TransferResult.Fault(fault)));

        Assert.Equal(new EndpointOutput.Fail(A, fault), Assert.Single(outputs));
    }

    // ── Rule 7: a cancelled transfer that was issued keeps running ─────

    [Fact]
    public void Cancelling_an_issued_transfer_abandons_it()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));

        var (s2, outputs) = Step(s1, Cancel(A));

        Assert.Equal(new EndpointOutput.Cancel(A), Assert.Single(outputs));
        Assert.Equal(new InFlightOrdered(T1, Owner: null), s2.InFlight);
    }

    // ── Rules 5 and 8: wait for an abandoned transfer to settle ────────

    [Fact]
    public void A_request_waits_for_an_abandoned_transfer_before_it_is_issued()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, Cancel(A));

        var (s3, waiting) = Step(s2, Request(B, 9));
        var (_, settled) = Step(s3, Settle(T1, TransferResult.Ok.Written(16)));

        Assert.Empty(waiting);
        Assert.Equal(new EndpointOutput.Issue(T2, B, 9), Assert.Single(settled));
    }

    [Fact]
    public void An_abandoned_control_reads_data_is_never_given_to_the_next_caller()
    {
        var upload = TransferResult.Ok.Read(Enumerable.Repeat((byte)0xAA, 64).ToArray());
        var status = TransferResult.Ok.Read(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00 });
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A, 64));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, Request(B, 6));

        var (s4, afterUpload) = Step(s3, Settle(T1, upload));
        var (_, afterStatus) = Step(s4, Settle(T2, status));

        Assert.DoesNotContain(afterUpload, o => o is EndpointOutput.Complete);
        Assert.Equal(new EndpointOutput.Complete(B, status), Assert.Single(afterStatus));
    }

    [Fact]
    public void An_abandoned_transfers_fault_is_discarded()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, Request(B));

        var (_, outputs) = Step(s3, Settle(T1, new TransferResult.Fault(new IOException("stall"))));

        Assert.Equal(new EndpointOutput.Issue(T2, B, 16), Assert.Single(outputs));
    }

    [Fact]
    public void An_abandoned_transfer_settling_with_no_one_waiting_leaves_the_endpoint_idle()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, Cancel(A));

        var (s3, settled) = Step(s2, Settle(T1, TransferResult.Ok.Written(16)));
        var (_, next) = Step(s3, Request(B));

        Assert.Empty(settled);
        Assert.Equal(new EndpointOutput.Issue(T2, B, 16), Assert.Single(next));
    }

    // ── Rule 6: a caller cancelled while waiting is never issued ───────

    [Fact]
    public void A_caller_cancelled_while_waiting_is_dropped_and_never_issued()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, Request(B));

        var (s4, cancelled) = Step(s3, Cancel(B));
        var (s5, settled) = Step(s4, Settle(T1, TransferResult.Ok.Written(16)));

        Assert.Equal(new EndpointOutput.Cancel(B), Assert.Single(cancelled));
        Assert.Empty(settled);
        Assert.Equal(1, s5.TransfersIssued);
    }

    // ── Inputs that do not apply ───────────────────────────────────────

    [Fact]
    public void A_second_caller_while_one_owns_the_transfer_is_rejected()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));

        var (s2, outputs) = Step(s1, Request(B));

        Assert.Equal(new EndpointOutput.RejectConcurrent(B), Assert.Single(outputs));
        Assert.Same(s1, s2);
    }

    [Fact]
    public void A_second_caller_while_one_waits_is_rejected()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, Request(B));

        var (s4, outputs) = Step(s3, Request(C));

        Assert.Equal(new EndpointOutput.RejectConcurrent(C), Assert.Single(outputs));
        Assert.Same(s3, s4);
    }

    [Fact]
    public void A_settle_for_another_transfer_is_ignored()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));

        var (s2, outputs) = Step(s1, Settle(T2, TransferResult.Ok.Written(1)));

        Assert.Empty(outputs);
        Assert.Same(s1, s2);
    }

    // ── Rule 10: disposal ──────────────────────────────────────────────

    [Fact]
    public void Disposal_fails_the_owner()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));

        var (_, outputs) = Step(s1, EndpointInput.Disposed.Instance);

        Assert.Equal(new EndpointOutput.FailDisposed(A), Assert.Single(outputs));
    }

    [Fact]
    public void Disposal_fails_the_waiting_caller()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, Cancel(A));
        var (s3, _) = Step(s2, Request(B));

        var (s4, outputs) = Step(s3, EndpointInput.Disposed.Instance);

        Assert.Equal(new EndpointOutput.FailDisposed(B), Assert.Single(outputs));
        Assert.Null(s4.InFlight);
        Assert.Null(s4.Waiting);
    }

    [Fact]
    public void After_disposal_requests_fail_and_settles_are_ignored()
    {
        var (s1, _) = Step(OrderedEndpointState.Idle, Request(A));
        var (s2, _) = Step(s1, EndpointInput.Disposed.Instance);

        var (s3, request) = Step(s2, Request(B));
        var (_, settle) = Step(s3, Settle(T1, TransferResult.Ok.Written(16)));

        Assert.Equal(new EndpointOutput.FailDisposed(B), Assert.Single(request));
        Assert.Empty(settle);
    }
}
