// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>What one probe cycle found. A closed union.</summary>
public abstract record ProbeOutcome
{
    private protected ProbeOutcome() { }

    /// <summary>A bootloader answered and identified itself.</summary>
    public sealed record Occupied(DeviceIdentity Identity) : ProbeOutcome;

    /// <summary>
    /// The probe was sent and nothing came back. Deliberately not called "absent": silence is also
    /// a seated but unresponsive board, a non-target device on that bridge, swapped RX/TX, a part
    /// held in reset, or one that has left the bootloader for its application — the last being the
    /// expected end of every successful flash.
    /// </summary>
    public sealed record NoResponse : ProbeOutcome
    {
        public static readonly NoResponse Instance = new();
    }

    /// <summary>The port itself failed — closed, unplugged, claimed by something else.</summary>
    public sealed record TransportFailed(string Message) : ProbeOutcome;
}

/// <summary>What the loop should do with a probe row after a cycle. A closed union.</summary>
public abstract record ProbeRowAction
{
    private protected ProbeRowAction() { }

    /// <summary>Nothing changed that anyone needs to hear about.</summary>
    public sealed record None : ProbeRowAction
    {
        public static readonly None Instance = new();
    }

    /// <summary>A target became present on this bridge.</summary>
    public sealed record Detected(DeviceIdentity Identity) : ProbeRowAction;

    /// <summary>The target on this bridge is gone.</summary>
    public sealed record Removed : ProbeRowAction
    {
        public static readonly Removed Instance = new();
    }

    /// <summary>The bridge itself failed; the loop stops and the operator is told why.</summary>
    public sealed record Faulted(string Message) : ProbeRowAction;
}

/// <summary>
/// The observable state of one bound bridge's row.
/// </summary>
/// <param name="Occupied">A bootloader answered on the last cycle.</param>
/// <param name="Silences">Consecutive no-response cycles; zero whenever something answered.</param>
/// <param name="Reported">
/// The identity currently claimed for this row, or <see langword="null"/> when nothing is claimed.
/// Held rather than reduced to a flag so a changed answer can be noticed: a fixture swapped inside
/// the retraction window never goes silent long enough to retract, and without the previous
/// identity to compare against, the new part would never be reported at all.
/// </param>
public readonly record struct ProbeRowState(bool Occupied, int Silences, DeviceIdentity? Reported)
{
    /// <summary>Before the first cycle: nothing observed, nothing claimed.</summary>
    public static readonly ProbeRowState Initial = new(false, 0, null);

    /// <summary>
    /// True once the row has been silent long enough that probing should slow down. The row is not
    /// "empty" — see <see cref="ProbeOutcome.NoResponse"/> — it is unheard from, and an armed
    /// fixture with no board in it is the normal resting state of this feature.
    /// </summary>
    public bool Stalled => Silences >= ProbeRowPolicy.SilencesBeforeBackoff;
}

/// <summary>
/// The pure, total probe-row decision (ADR-0052): given the row's state and what one cycle found,
/// decide the next state and what to tell the app. Same inputs -> same decision; no IO, no clock.
/// <para>
/// The safety-relevant part is that presence is only ever claimed from an answer, while absence
/// needs several consecutive silences — because a single silence is routine. A part resets, a
/// bridge drops a byte, a flash finishes and the part jumps to its application. Retracting a row
/// on the first quiet cycle would make the row flicker.
/// </para>
/// <para>
/// <b>What this does not decide.</b> <see cref="ProbeRowAction.Removed"/> retracts a <i>row</i>; it
/// does not reopen the autoflash dedupe gate. adr.md Decision 10 defaults to one flash per bound
/// bridge per armed session - Decision 5 unchanged - and only <c>--repeat</c> reopens it, with
/// <c>--repeat=cts</c> requiring a present-detect line rather than inference. A board that goes
/// quiet for three cycles while seated is therefore re-reported here and still not re-flashed,
/// unless the operator asked for a fixture loop. Where they have, the residual is
/// <see cref="ProbeOutcome.NoResponse"/>: silence cannot tell a reset from a departure, which is
/// why the driving loop must make <see cref="SilencesBeforeRemoved"/> cycles comfortably longer
/// than a part takes to reset.
/// </para>
/// </summary>
public static class ProbeRowPolicy
{
    /// <summary>Consecutive silences before the row is reported gone.</summary>
    public const int SilencesBeforeRemoved = 3;

    /// <summary>
    /// Consecutive silences before probing slows down. Deliberately much larger than
    /// <see cref="SilencesBeforeRemoved"/>: reporting a board gone should be prompt, while backing
    /// off is about a fixture that has been sitting empty, where the cost is bytes going to
    /// whatever is attached rather than a stale row.
    /// </summary>
    public const int SilencesBeforeBackoff = 20;

    /// <summary>Advances one row by one cycle.</summary>
    public static (ProbeRowState State, ProbeRowAction Action) Advance(ProbeRowState state, ProbeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome)
        {
            case ProbeOutcome.Occupied occupied:
                // An answer is the only thing that establishes presence, and it resets everything:
                // the silence run, the backoff, and — if the row had been retracted — the claim.
                //
                // A *changed* answer re-reports. A board swapped inside the retraction window never
                // goes quiet long enough to retract the row, so comparing against the claimed
                // identity is the only thing that notices it. This cannot separate two boards of the
                // same part number - both answer 0x468, which is the whole reason adr.md Decision 10
                // gates on departure rather than identity - but it does catch a different part
                // appearing on the fixture, and reporting that is strictly better than not.
                bool isNew = state.Reported is not { } claimed || claimed != occupied.Identity;
                return (new ProbeRowState(Occupied: true, Silences: 0, Reported: occupied.Identity),
                        isNew ? new ProbeRowAction.Detected(occupied.Identity) : ProbeRowAction.None.Instance);

            case ProbeOutcome.NoResponse:
                int silences = state.Silences + 1;
                bool retract = state.Reported is not null && silences >= SilencesBeforeRemoved;
                return (new ProbeRowState(Occupied: false, Silences: silences, Reported: retract ? null : state.Reported),
                        retract ? ProbeRowAction.Removed.Instance : ProbeRowAction.None.Instance);

            case ProbeOutcome.TransportFailed failed:
                // Not silence, and not recoverable by probing harder. The bridge is gone or unusable;
                // the loop stops rather than hammering a dead port, and the row says why.
                return (ProbeRowState.Initial, new ProbeRowAction.Faulted(failed.Message));

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unhandled probe outcome");
        }
    }
}
