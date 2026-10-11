// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Usb.Browser.Core;

/// <summary>
/// The WebUSB transfer outstanding on an ordered endpoint. <see cref="Owner"/> is the caller it
/// will complete, or <see langword="null"/> when that caller gave up.
/// </summary>
internal readonly record struct InFlightOrdered(TransferId Id, CallerId? Owner);

/// <summary>
/// The state of a bulk or interrupt OUT endpoint, or of the control endpoint (ADR-0092 D7, rules 5
/// to 9).
/// </summary>
internal sealed record OrderedEndpointState
{
    /// <summary>The state of an idle endpoint.</summary>
    public static OrderedEndpointState Idle { get; } = new();

    /// <summary>The one outstanding transfer, if any.</summary>
    public InFlightOrdered? InFlight { get; init; }

    /// <summary>
    /// The caller waiting for an abandoned transfer to settle before its own is issued, and the
    /// length it asked for.
    /// </summary>
    public (CallerId Caller, int Length)? Waiting { get; init; }

    /// <summary>How many transfers this endpoint has issued. The next transfer's id is this plus one.</summary>
    public int TransfersIssued { get; init; }

    /// <summary>Whether the backend has been disposed. A disposed endpoint fails every request.</summary>
    public bool IsDisposed { get; init; }
}

/// <summary>
/// The pure state machine for an endpoint whose transfers must reach the device in order: bulk and
/// interrupt OUT, and control on endpoint 0 (ADR-0092 D7).
/// </summary>
/// <remarks>
/// <para>A caller that gives up leaves its transfer running, because WebUSB cannot cancel one.</para>
/// <list type="bullet">
/// <item>A new transfer waits for an abandoned one to settle before it is issued, so transfers
/// reach the device in the order callers asked for them (rules 5 and 8).</item>
/// <item>A caller cancelled while it waits is dropped and its transfer never issued (rule 6).</item>
/// <item>An abandoned transfer's result is discarded. A control transfer never takes over and
/// never holds, so a DFU <c>UPLOAD</c>'s bytes are never read as the reply to a later
/// <c>GETSTATUS</c> (rule 8).</item>
/// <item>A transfer that was issued before its caller gave up may still reach the device
/// (rules 7 and 9).</item>
/// </list>
/// </remarks>
internal static class OrderedEndpoint
{
    /// <summary>Advances <paramref name="state"/> by one input.</summary>
    public static (OrderedEndpointState Next, ImmutableArray<EndpointOutput> Outputs) Step(
        OrderedEndpointState state, EndpointInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);

        return input switch
        {
            EndpointInput.Requested r => OnRequested(state, r),
            EndpointInput.Settled s => OnSettled(state, s),
            EndpointInput.Cancelled c => OnCancelled(state, c),
            EndpointInput.Disposed => OnDisposed(state),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown endpoint input."),
        };
    }

    private static (OrderedEndpointState, ImmutableArray<EndpointOutput>) OnRequested(
        OrderedEndpointState state, EndpointInput.Requested request)
    {
        var caller = request.Caller;

        if (state.IsDisposed)
            return (state, [new EndpointOutput.FailDisposed(caller)]);

        if (state.InFlight is { Owner: not null } || state.Waiting is not null)
            return (state, [new EndpointOutput.RejectConcurrent(caller)]);

        if (state.InFlight is not null)
            return (state with { Waiting = (caller, request.Length) }, []);

        return Issue(state, caller, request.Length);
    }

    private static (OrderedEndpointState, ImmutableArray<EndpointOutput>) OnSettled(
        OrderedEndpointState state, EndpointInput.Settled settled)
    {
        if (state.IsDisposed || state.InFlight is not { } inFlight || inFlight.Id != settled.Transfer)
            return (state, []);

        var idle = state with { InFlight = null };

        if (inFlight.Owner is { } owner)
        {
            EndpointOutput output = settled.Result switch
            {
                TransferResult.Ok ok => new EndpointOutput.Complete(owner, ok),
                TransferResult.Fault f => new EndpointOutput.Fail(owner, f.Error),
                _ => throw new ArgumentOutOfRangeException(nameof(settled), settled.Result, "Unknown transfer result."),
            };
            return (idle, [output]);
        }

        // Abandoned: its result is discarded, and the caller waiting behind it goes next.
        if (idle.Waiting is { } waiting)
            return Issue(idle with { Waiting = null }, waiting.Caller, waiting.Length);

        return (idle, []);
    }

    private static (OrderedEndpointState, ImmutableArray<EndpointOutput>) OnCancelled(
        OrderedEndpointState state, EndpointInput.Cancelled cancelled)
    {
        var caller = cancelled.Caller;

        if (state.InFlight is { Owner: { } owner } inFlight && owner == caller)
            return (state with { InFlight = inFlight with { Owner = null } }, [new EndpointOutput.Cancel(caller)]);

        if (state.Waiting is { } waiting && waiting.Caller == caller)
            return (state with { Waiting = null }, [new EndpointOutput.Cancel(caller)]);

        return (state, []);
    }

    private static (OrderedEndpointState, ImmutableArray<EndpointOutput>) OnDisposed(OrderedEndpointState state)
    {
        if (state.IsDisposed)
            return (state, []);

        var outputs = ImmutableArray.CreateBuilder<EndpointOutput>();
        if (state.InFlight is { Owner: { } owner })
            outputs.Add(new EndpointOutput.FailDisposed(owner));
        if (state.Waiting is { } waiting)
            outputs.Add(new EndpointOutput.FailDisposed(waiting.Caller));

        return (state with { InFlight = null, Waiting = null, IsDisposed = true }, outputs.ToImmutable());
    }

    private static (OrderedEndpointState, ImmutableArray<EndpointOutput>) Issue(
        OrderedEndpointState state, CallerId caller, int length)
    {
        var id = new TransferId(state.TransfersIssued + 1);
        return (state with { InFlight = new InFlightOrdered(id, caller), TransfersIssued = id.Value },
            [new EndpointOutput.Issue(id, caller, length)]);
    }
}
