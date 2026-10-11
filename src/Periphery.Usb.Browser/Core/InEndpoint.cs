// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Usb.Browser.Core;

/// <summary>A caller waiting on an IN transfer, and the most bytes it can take.</summary>
internal readonly record struct InReader(CallerId Caller, int Length);

/// <summary>
/// The WebUSB <c>transferIn</c> outstanding on an endpoint. <see cref="Owner"/> is the caller it
/// will complete, or <see langword="null"/> when the caller that issued it gave up.
/// </summary>
internal readonly record struct InFlightIn(TransferId Id, int Length, InReader? Owner);

/// <summary>The state of one bulk or interrupt IN endpoint (ADR-0092 D7, rules 1 to 4).</summary>
internal sealed record InEndpointState
{
    /// <summary>Creates the state of an idle endpoint whose <c>wMaxPacketSize</c> is <paramref name="maxPacketSize"/>.</summary>
    public InEndpointState(int maxPacketSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPacketSize);
        MaxPacketSize = maxPacketSize;
    }

    /// <summary>The endpoint's <c>wMaxPacketSize</c>.</summary>
    public int MaxPacketSize { get; }

    /// <summary>The one outstanding <c>transferIn</c>, if any.</summary>
    public InFlightIn? InFlight { get; init; }

    /// <summary>
    /// Bytes the host has taken from the device that no caller has read yet. Empty whenever a
    /// transfer is outstanding, because a read drains it before issuing one.
    /// </summary>
    public ReadOnlyMemory<byte> Held { get; init; }

    /// <summary>The fault an abandoned transfer ended in, kept for the next read.</summary>
    public Exception? HeldFault { get; init; }

    /// <summary>How many transfers this endpoint has issued. The next transfer's id is this plus one.</summary>
    public int TransfersIssued { get; init; }

    /// <summary>Whether the backend has been disposed. A disposed endpoint fails every request.</summary>
    public bool IsDisposed { get; init; }
}

/// <summary>
/// The pure state machine for a bulk or interrupt IN endpoint (ADR-0092 D7).
/// </summary>
/// <remarks>
/// <para>WebUSB cannot cancel one transfer; only closing the device aborts it. So a caller that
/// gives up leaves its <c>transferIn</c> running, and this machine decides where its bytes go:</para>
/// <list type="number">
/// <item>Every transfer asks for the caller's length rounded up to <c>wMaxPacketSize</c>. A
/// transfer that ends on a packet boundary cannot babble.</item>
/// <item>At most one transfer is outstanding. A read that finds an abandoned one takes it over
/// instead of issuing another, which would take the next packet out of order.</item>
/// <item>Bytes beyond the reading caller's buffer are held, and the next read drains them before
/// it issues a transfer. The host has already taken them from the device, so dropping them would
/// lose data.</item>
/// <item>An abandoned transfer that ends in a fault holds the fault, and the next read gets it.</item>
/// </list>
/// </remarks>
internal static class InEndpoint
{
    /// <summary>Advances <paramref name="state"/> by one input.</summary>
    public static (InEndpointState Next, ImmutableArray<EndpointOutput> Outputs) Step(
        InEndpointState state, EndpointInput input)
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

    /// <summary>
    /// The largest transfer Chromium accepts, 32 MiB. A longer <c>transferIn</c> rejects with
    /// <c>DataError</c> (<c>kUsbTransferLengthLimit</c> in <c>usb_device.cc</c>).
    /// </summary>
    internal const int MaxTransferLength = 32 * 1024 * 1024;

    /// <summary>
    /// <paramref name="length"/> rounded up to a whole number of <paramref name="maxPacketSize"/>
    /// packets, and at least one packet, capped at the last packet boundary within
    /// <see cref="MaxTransferLength"/>.
    /// </summary>
    /// <remarks>
    /// Under the cap the result is never less than <paramref name="length"/>. A read longer than
    /// the cap is issued at the cap and returns at most that much, as a short read. Asking for more
    /// would only be refused.
    /// </remarks>
    internal static int RoundUpToPacket(int length, int maxPacketSize)
    {
        long packets = Math.Max(1L, ((long)length + maxPacketSize - 1) / maxPacketSize);
        long rounded = packets * maxPacketSize;
        return rounded <= MaxTransferLength
            ? (int)rounded
            : MaxTransferLength - (MaxTransferLength % maxPacketSize);
    }

    private static (InEndpointState, ImmutableArray<EndpointOutput>) OnRequested(
        InEndpointState state, EndpointInput.Requested request)
    {
        var caller = request.Caller;

        if (state.IsDisposed)
            return (state, [new EndpointOutput.FailDisposed(caller)]);

        if (state.InFlight is { Owner: not null })
            return (state, [new EndpointOutput.RejectConcurrent(caller)]);

        int length = Math.Max(0, request.Length);

        if (!state.Held.IsEmpty)
        {
            int take = Math.Min(length, state.Held.Length);
            var bytes = state.Held[..take];
            return (state with { Held = state.Held[take..] },
                [new EndpointOutput.Complete(caller, TransferResult.Ok.Read(bytes))]);
        }

        if (state.HeldFault is { } fault)
            return (state with { HeldFault = null }, [new EndpointOutput.Fail(caller, fault)]);

        var reader = new InReader(caller, length);

        if (state.InFlight is { } abandoned)
            return (state with { InFlight = abandoned with { Owner = reader } }, []);

        var id = new TransferId(state.TransfersIssued + 1);
        int issued = RoundUpToPacket(length, state.MaxPacketSize);
        return (state with
            {
                InFlight = new InFlightIn(id, issued, reader),
                TransfersIssued = id.Value,
            },
            [new EndpointOutput.Issue(id, caller, issued)]);
    }

    private static (InEndpointState, ImmutableArray<EndpointOutput>) OnSettled(
        InEndpointState state, EndpointInput.Settled settled)
    {
        if (state.IsDisposed || state.InFlight is not { } inFlight || inFlight.Id != settled.Transfer)
            return (state, []);

        var idle = state with { InFlight = null };

        if (inFlight.Owner is { } owner)
        {
            return settled.Result switch
            {
                TransferResult.Ok ok => Deliver(idle, owner, ok.Data),
                TransferResult.Fault f => (idle, [new EndpointOutput.Fail(owner.Caller, f.Error)]),
                _ => throw UnknownResult(settled),
            };
        }

        return settled.Result switch
        {
            TransferResult.Ok ok => (idle with { Held = Append(state.Held, ok.Data) }, []),
            TransferResult.Fault f => (idle with { HeldFault = f.Error }, []),
            _ => throw UnknownResult(settled),
        };
    }

    private static (InEndpointState, ImmutableArray<EndpointOutput>) Deliver(
        InEndpointState idle, InReader owner, ReadOnlyMemory<byte> data)
    {
        int give = Math.Min(owner.Length, data.Length);
        return (idle with { Held = Append(idle.Held, data[give..]) },
            [new EndpointOutput.Complete(owner.Caller, TransferResult.Ok.Read(data[..give]))]);
    }

    private static ArgumentOutOfRangeException UnknownResult(EndpointInput.Settled settled)
        => new(nameof(settled), settled.Result, "Unknown transfer result.");

    private static (InEndpointState, ImmutableArray<EndpointOutput>) OnCancelled(
        InEndpointState state, EndpointInput.Cancelled cancelled)
    {
        if (state.InFlight is { Owner: { } owner } inFlight && owner.Caller == cancelled.Caller)
        {
            return (state with { InFlight = inFlight with { Owner = null } },
                [new EndpointOutput.Cancel(owner.Caller)]);
        }

        // The caller already has its result, or never got past the request.
        return (state, []);
    }

    private static (InEndpointState, ImmutableArray<EndpointOutput>) OnDisposed(InEndpointState state)
    {
        if (state.IsDisposed)
            return (state, []);

        var outputs = state.InFlight is { Owner: { } owner }
            ? ImmutableArray.Create<EndpointOutput>(new EndpointOutput.FailDisposed(owner.Caller))
            : ImmutableArray<EndpointOutput>.Empty;

        return (state with
            {
                InFlight = null,
                Held = ReadOnlyMemory<byte>.Empty,
                HeldFault = null,
                IsDisposed = true,
            },
            outputs);
    }

    // Held is empty whenever a transfer is outstanding, so this copies only if that ever stops
    // being true.
    private static ReadOnlyMemory<byte> Append(ReadOnlyMemory<byte> held, ReadOnlyMemory<byte> more)
    {
        if (held.IsEmpty) return more;
        if (more.IsEmpty) return held;
        var joined = new byte[held.Length + more.Length];
        held.CopyTo(joined);
        more.CopyTo(joined.AsMemory(held.Length));
        return joined;
    }
}
