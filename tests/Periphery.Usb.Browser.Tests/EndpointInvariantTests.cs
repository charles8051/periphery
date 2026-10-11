using System.Collections.Immutable;

namespace Periphery.Usb.Browser.Tests;

/// <summary>
/// Drives each endpoint machine through seeded random sequences of requests, settles and
/// cancellations, playing the shell and the device, and checks the invariants ADR-0092 D7 rests
/// on after every step. The seed fixes the sequence, so a failure replays exactly.
/// </summary>
public class EndpointInvariantTests
{
    private const int Steps = 400;

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 1; seed <= 64; seed++)
            data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void An_in_endpoint_delivers_every_byte_once_and_in_order(int seed)
    {
        var rng = new Random(seed);
        int mps = rng.Next(2) == 0 ? 8 : 64;
        var state = new InEndpointState(mps);

        var produced = new List<byte>();
        var delivered = new List<byte>();
        var faultsProduced = new List<Exception>();
        var faultsDelivered = new List<Exception>();
        var lengths = new Dictionary<CallerId, int>();
        var terminal = new HashSet<CallerId>();
        (TransferId Id, int Length)? outstanding = null;
        int nextCaller = 1;
        byte nextByte = 0;

        void Apply(EndpointInput input)
        {
            ImmutableArray<EndpointOutput> outputs;
            (state, outputs) = InEndpoint.Step(state, input);

            if (input is EndpointInput.Settled settled && outstanding?.Id == settled.Transfer)
                outstanding = null;

            foreach (var output in outputs)
            {
                switch (output)
                {
                    case EndpointOutput.Issue issue:
                        Assert.Null(outstanding);
                        Assert.Equal(0, issue.Length % mps);
                        Assert.True(issue.Length >= lengths[issue.Caller]);
                        outstanding = (issue.Transfer, issue.Length);
                        break;
                    case EndpointOutput.Complete complete:
                        Assert.True(terminal.Add(complete.Caller));
                        Assert.True(complete.Result.Data.Length <= lengths[complete.Caller]);
                        delivered.AddRange(complete.Result.Data.ToArray());
                        break;
                    case EndpointOutput.Fail fail:
                        Assert.True(terminal.Add(fail.Caller));
                        faultsDelivered.Add(fail.Error);
                        break;
                    case EndpointOutput.Cancel c:
                        Assert.True(terminal.Add(c.Caller));
                        break;
                    case EndpointOutput.FailDisposed d:
                        Assert.True(terminal.Add(d.Caller));
                        break;
                    case EndpointOutput.RejectConcurrent r:
                        Assert.True(terminal.Add(r.Caller));
                        break;
                }
            }

            if (state.IsDisposed)
                return;

            // Rule 3: nothing is lost or duplicated. What callers got, then what is held, is
            // exactly what the device sent.
            Assert.True(produced.SequenceEqual(delivered.Concat(state.Held.ToArray())),
                $"seed {seed}: delivered and held bytes differ from what the device sent");

            // Rule 4: a fault is delivered once, or is still held.
            var accounted = state.HeldFault is { } held ? faultsDelivered.Append(held) : faultsDelivered;
            Assert.Equal(faultsProduced, accounted);

            // Rule 2: held bytes are drained before a transfer is issued.
            Assert.True(state.InFlight is null || state.Held.IsEmpty);
        }

        for (int step = 0; step < Steps; step++)
        {
            int roll = rng.Next(100);
            if (roll < 35)
            {
                var caller = new CallerId(nextCaller++);
                lengths[caller] = rng.Next(0, 3 * mps);
                Apply(new EndpointInput.Requested(caller, lengths[caller]));
            }
            else if (roll < 75 && outstanding is { } o)
            {
                if (rng.Next(100) < 15)
                {
                    var fault = new IOException($"fault at step {step}");
                    faultsProduced.Add(fault);
                    Apply(new EndpointInput.Settled(o.Id, new TransferResult.Fault(fault)));
                }
                else
                {
                    var data = new byte[rng.Next(0, o.Length + 1)];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = nextByte++;
                    produced.AddRange(data);
                    Apply(new EndpointInput.Settled(o.Id, TransferResult.Ok.Read(data)));
                }
            }
            else if (roll < 95 && lengths.Count > 0)
            {
                // Usually the caller still waiting, sometimes one that already has its result.
                var pending = lengths.Keys.Where(c => !terminal.Contains(c)).ToList();
                var pool = pending.Count > 0 && rng.Next(4) != 0 ? pending : lengths.Keys.ToList();
                Apply(new EndpointInput.Cancelled(pool[rng.Next(pool.Count)]));
            }
        }

        Apply(EndpointInput.Disposed.Instance);

        // Rule 10: every caller ends with exactly one result.
        Assert.Equal(lengths.Keys.ToHashSet(), terminal);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void An_ordered_endpoint_issues_in_order_and_gives_each_caller_its_own_result(int seed)
    {
        var rng = new Random(seed);
        var state = OrderedEndpointState.Idle;

        var requested = new List<CallerId>();
        var terminal = new HashSet<CallerId>();
        var issuedFor = new Dictionary<CallerId, TransferId>();
        var issueOrder = new List<CallerId>();
        var rejected = new HashSet<CallerId>();
        var cancelledUnissued = new HashSet<CallerId>();
        var faults = new Dictionary<TransferId, Exception>();
        TransferId? outstanding = null;
        int nextCaller = 1;

        void Apply(EndpointInput input)
        {
            ImmutableArray<EndpointOutput> outputs;
            (state, outputs) = OrderedEndpoint.Step(state, input);

            if (input is EndpointInput.Settled settled && outstanding == settled.Transfer)
                outstanding = null;

            foreach (var output in outputs)
            {
                switch (output)
                {
                    case EndpointOutput.Issue issue:
                        // Rules 5 and 8: one transfer at a time.
                        Assert.Null(outstanding);
                        Assert.DoesNotContain(issue.Caller, terminal);
                        Assert.True(issuedFor.TryAdd(issue.Caller, issue.Transfer));
                        issueOrder.Add(issue.Caller);
                        outstanding = issue.Transfer;
                        break;
                    case EndpointOutput.Complete complete:
                        Assert.True(terminal.Add(complete.Caller));
                        // Rule 8: the result is the caller's own transfer's, never an abandoned one's.
                        Assert.Equal(issuedFor[complete.Caller].Value, complete.Result.Count);
                        break;
                    case EndpointOutput.Fail fail:
                        Assert.True(terminal.Add(fail.Caller));
                        Assert.Same(faults[issuedFor[fail.Caller]], fail.Error);
                        break;
                    case EndpointOutput.Cancel c:
                        Assert.True(terminal.Add(c.Caller));
                        if (!issuedFor.ContainsKey(c.Caller))
                            cancelledUnissued.Add(c.Caller);
                        break;
                    case EndpointOutput.FailDisposed d:
                        Assert.True(terminal.Add(d.Caller));
                        break;
                    case EndpointOutput.RejectConcurrent r:
                        Assert.True(terminal.Add(r.Caller));
                        rejected.Add(r.Caller);
                        break;
                }
            }
        }

        for (int step = 0; step < Steps; step++)
        {
            int roll = rng.Next(100);
            if (roll < 35)
            {
                var caller = new CallerId(nextCaller++);
                requested.Add(caller);
                Apply(new EndpointInput.Requested(caller, rng.Next(0, 512)));
            }
            else if (roll < 75 && outstanding is { } t)
            {
                if (rng.Next(100) < 15)
                {
                    faults[t] = new IOException($"fault on transfer {t.Value}");
                    Apply(new EndpointInput.Settled(t, new TransferResult.Fault(faults[t])));
                }
                else
                {
                    // The count names the transfer, so a misrouted result is visible.
                    Apply(new EndpointInput.Settled(t, TransferResult.Ok.Written(t.Value)));
                }
            }
            else if (roll < 95 && requested.Count > 0)
            {
                var pending = requested.Where(c => !terminal.Contains(c)).ToList();
                var pool = pending.Count > 0 && rng.Next(4) != 0 ? pending : requested;
                Apply(new EndpointInput.Cancelled(pool[rng.Next(pool.Count)]));
            }
        }

        Apply(EndpointInput.Disposed.Instance);

        // Rule 6: a caller cancelled before its transfer was issued never had one issued.
        Assert.DoesNotContain(cancelledUnissued, issuedFor.ContainsKey);

        // Transfers went out in the order callers were accepted.
        var accepted = requested.Where(c => !rejected.Contains(c) && issuedFor.ContainsKey(c)).ToList();
        Assert.Equal(accepted, issueOrder);

        // Rule 10: every caller ends with exactly one result.
        Assert.Equal(requested.ToHashSet(), terminal);
    }
}
