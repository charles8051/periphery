// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// An <see cref="IRecoveryPolicy"/> that walks the device's advertised reset ladder,
/// gentlest first, one rung per attempt, and gives up when the rungs run out
/// (ADR-0076). The counterpart to <see cref="ExponentialBackoffRecoveryPolicy"/>:
/// that one only ever waits, this one only ever escalates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second policy rather than a knob on the first.</b> The backoff policy
/// answers "the device is busy or settling, wait longer"; it never returns
/// <see cref="RecoveryDirective.Reset"/>, so a caller that needs escalation cannot get
/// there by tuning it. The two express genuinely different beliefs about the fault, and
/// a fault where waiting cannot help is exactly where the reset ladder earns its keep —
/// see <see cref="RecoveryTrigger.BootloaderEntryFailure"/>, where re-sending a command
/// down a wedged endpoint fails identically however long you wait first.
/// </para>
/// <para>
/// <b>Pure and total</b> (ADR-0052): same <see cref="RecoveryContext"/> in, same
/// <see cref="RecoveryDirective"/> out. No IO, no clock, no cancellation. It never
/// throws — an out-of-range attempt or an empty ladder is answered with
/// <see cref="RecoveryDirective.GiveUp"/>, not an exception.
/// </para>
/// <para>
/// <b>It cannot invent a rung.</b> Every strategy it names is read out of
/// <see cref="RecoveryContext.AvailableResets"/>, which the shell fills from
/// <see cref="IDeviceReset.StrategiesFor"/>. <c>ResetEscalation.Decide</c> re-checks that
/// independently, so a device that advertises nothing gets
/// <see cref="RecoveryDirective.GiveUp"/> here and would be conceded there even if this
/// were wrong.
/// </para>
/// </remarks>
/// <param name="sanityRetries">
/// How many plain retries to spend before touching the ladder — the "rule out a blip"
/// allowance ADR-0060 Decision 3 describes. Default <c>1</c>: one repeat is cheap and
/// distinguishes a genuine wedge from a transient collision, while more than one just
/// spends the operator's time re-failing identically. Pass <c>0</c> to escalate on the
/// first failure.
/// </param>
/// <param name="retryDelay">
/// The delay carried by those sanity retries. Default 500 ms — long enough for a device
/// mid-re-enumeration to settle, short enough not to feel like a hang. As always the
/// policy only *schedules* the delay; the shell awaits it.
/// </param>
public sealed class EscalatingResetRecoveryPolicy(
    int sanityRetries = 1,
    TimeSpan? retryDelay = null) : IRecoveryPolicy
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The default for bootloader-entry recovery: one sanity retry, then every advertised
    /// rung gentlest-first, then give up.
    /// </summary>
    public static readonly IRecoveryPolicy Default = new EscalatingResetRecoveryPolicy();

    /// <inheritdoc/>
    public RecoveryDirective Decide(RecoveryContext context)
    {
        // Attempt is 1-based. Spend the sanity allowance first.
        if (context.Attempt <= sanityRetries)
            return new RecoveryDirective.Retry(_retryDelay);

        // Then one rung per subsequent attempt, gentlest first. AvailableResets is already
        // ordered that way by contract (IDeviceReset.StrategiesFor).
        int rung = context.Attempt - sanityRetries - 1;
        if (rung < 0 || rung >= context.AvailableResets.Count)
            return new RecoveryDirective.GiveUp();

        return new RecoveryDirective.Reset(context.AvailableResets[rung]);
    }
}
