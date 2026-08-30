// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery;

/// <summary>
/// Decides how <see cref="DeviceProxyBase{TDevice,TException}"/> recovers after a
/// session fails to open or drops: retry (with a delay), reset the device, or
/// give up. Supersedes <c>IReconnectPolicy</c> (ADR-0055), widening the decision
/// from <c>delay | give-up</c> to <c>retry | reset | give-up</c> (ADR-0060).
/// </summary>
/// <remarks>
/// <para>The seam is BCL-only by design — no third-party type crosses the
/// Periphery boundary. A consumer that wants jitter, decorrelated backoff, a
/// circuit breaker, or a fault-keyed reset signature implements this interface
/// (over Polly or otherwise) in <em>their</em> assembly.</para>
/// <para>The decision is a <b>pure, total function</b> of its input
/// (ADR-0052 functional core): the same <see cref="RecoveryContext"/>
/// yields the same <see cref="RecoveryDirective"/>, with no IO, no clock, no
/// <see cref="System.Threading.Tasks.Task"/>, and no
/// <see cref="System.Threading.CancellationToken"/>. <see cref="Decide"/> only
/// <em>schedules</em> the next step (it returns a <see cref="RecoveryDirective.Retry"/>
/// carrying the delay as a value); the shell — <see cref="DeviceProxyBase{TDevice,TException}"/> —
/// owns the single <c>await Task.Delay(directive.Delay, ct)</c> that enacts it, the
/// reopen-deadline clock, the cancellation token, and the reset mechanism. This is
/// the split ADR-0055 always stated ("State, IO, the clock … live in the proxy, not
/// the policy"); the seam now matches it.</para>
/// </remarks>
public interface IRecoveryPolicy
{
    /// <summary>
    /// Chooses the next recovery step after each open-failure (and after each reset
    /// that did not reopen). A pure schedule: the returned
    /// <see cref="RecoveryDirective.Retry"/> carries the delay the shell will
    /// <c>Task.Delay</c> on, not a delay this method performs. Must be total — no
    /// throwing for control flow — and free of IO, the clock, and cancellation,
    /// all of which the proxy (the imperative shell) owns.
    /// </summary>
    /// <param name="context">Inputs to the recovery decision.</param>
    RecoveryDirective Decide(RecoveryContext context);
}

/// <summary>
/// Inputs to a recovery decision. A pure value; same input → same decision.
/// </summary>
/// <param name="Attempt">
/// 1-based consecutive open-failure count this cycle; resets to 1 when the device
/// re-enumerates.
/// </param>
/// <param name="ResetCount">
/// Number of resets performed since the last stable open — the reset budget. A
/// policy bounds total resets with this (e.g. <c>ResetCount &lt; 2</c>) so a
/// device that re-wedges immediately still reaches <see cref="RecoveryDirective.GiveUp"/>.
/// </param>
/// <param name="LastFault">
/// The IO/open fault driving the decision, or <see langword="null"/> for a plain
/// open-failure that produced no exception. This is the <em>reset-early</em>
/// signal: a policy that recognizes a wedge signature can return
/// <see cref="RecoveryDirective.Reset"/> on attempt 1.
/// </param>
/// <param name="Device">The still-enumerated device we are failing to open.</param>
/// <param name="AvailableResets">
/// The reset strategies this device can attempt, gentlest first (from
/// <see cref="IDeviceReset.StrategiesFor"/>). <b>Empty ⇒ reset is not an option</b>
/// — a policy must not return <see cref="RecoveryDirective.Reset"/>.
/// </param>
/// <param name="Trigger">
/// What drove this recovery decision (ADR-0060 Decision 11):
/// <see cref="RecoveryTrigger.OpenFailure"/> (a session failed to open or dropped on
/// an Active device — the original ADR-0055/0060 path) or
/// <see cref="RecoveryTrigger.EnumeratedFault"/> (the device enumerated but reported a
/// genuine OS-level fault and never reached a stable open). Lets a policy treat a
/// faulted-but-never-ready node differently from a failing open — e.g. reset
/// immediately rather than walking a retry ladder there is nothing yet to retry
/// <em>against</em>. Defaults to <see cref="RecoveryTrigger.OpenFailure"/>.
/// </param>
public readonly record struct RecoveryContext(
    int Attempt,
    int ResetCount,
    Exception? LastFault,
    DeviceInfo Device,
    IReadOnlyList<ResetStrategy> AvailableResets,
    RecoveryTrigger Trigger = RecoveryTrigger.OpenFailure);

/// <summary>
/// Why <see cref="DeviceProxyBase{TDevice,TException}"/> is running the recovery
/// seam — the discriminator on <see cref="RecoveryContext.Trigger"/> (ADR-0060
/// Decision 11).
/// </summary>
public enum RecoveryTrigger
{
    /// <summary>
    /// A session failed to open, or a live session dropped, on a device the tracker
    /// reports as <see cref="DeviceActivityStatus.Active"/>. The original ADR-0055 /
    /// ADR-0060 recovery path; the retry ladder re-opens the same enumerated handle.
    /// </summary>
    OpenFailure = 0,

    /// <summary>
    /// The device is enumerated but never reached <see cref="DeviceActivityStatus.Active"/>
    /// and reports a genuine OS-level fault (<see cref="DeviceStatus.Error"/> with a
    /// resettable problem code — see <see cref="DeviceFaultClassifier"/>). There is no
    /// healthy handle to re-open; recovery must clear the devnode (reset) and wait for
    /// it to come up Active. Opt-in — see the faulted-node recovery gating on the proxy.
    /// </summary>
    EnumeratedFault = 1,

    /// <summary>
    /// A firmware update could not put the device into its bootloader — the mode switch
    /// itself failed (ADR-0076). Unlike the other two triggers this does not come from
    /// <see cref="DeviceProxyBase{TDevice,TException}"/> at all; it comes from
    /// <c>BootloaderEntryOrchestrator</c>, which drives the same seam because "the device
    /// will not do what it is told" is the same problem the seam already solves.
    /// <para>
    /// <b>A plain retry is close to worthless here</b>, and a policy should treat this
    /// trigger accordingly. The mode switch travels over the device's normal data path, so
    /// the dominant failure is that path being wedged — re-sending the same command down
    /// the same wedged endpoint fails the same way. Recovery has to make the device *able*
    /// to hear the command, which means a reset. See
    /// <see cref="EscalatingResetRecoveryPolicy"/>.
    /// </para>
    /// </summary>
    BootloaderEntryFailure = 2,
}

/// <summary>
/// The step an <see cref="IRecoveryPolicy"/> chooses: a closed hierarchy of
/// <see cref="Retry"/>, <see cref="Reset"/>, or <see cref="GiveUp"/>.
/// </summary>
public abstract record RecoveryDirective
{
    // Private ctor closes the hierarchy: only the nested cases below can derive.
    private RecoveryDirective() { }

    /// <summary>Wait <see cref="Delay"/>, then attempt to re-open.</summary>
    public sealed record Retry(TimeSpan Delay) : RecoveryDirective;

    /// <summary>
    /// Reset the device with <see cref="Strategy"/> (one of
    /// <see cref="RecoveryContext.AvailableResets"/>), then re-open. The proxy
    /// drives the re-open itself (ADR-0060 Decision 9).
    /// </summary>
    public sealed record Reset(ResetStrategy Strategy) : RecoveryDirective;

    /// <summary>
    /// Stop. The proxy transitions to <see cref="ConnectionState.GaveUp"/> and
    /// parks until the device re-enumerates (which resets the attempt budget).
    /// </summary>
    public sealed record GiveUp : RecoveryDirective;
}
