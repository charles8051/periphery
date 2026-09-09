// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// A bounded "wait until this is true" poll — the shell-side half of a readiness
/// check (ADR-0052): it owns the clock and the delay, while the predicate it is
/// handed stays a pure question about observed state.
/// </summary>
/// <remarks>
/// Exists because some platform transitions produce <b>no observable edge</b> to
/// wait on. A Windows <c>CM_Disable_DevNode</c>/<c>CM_Enable_DevNode</c> cycle is
/// the motivating case (periphery #251): the devnode never leaves the tree, so no
/// watcher notification fires and the only way to learn the driver stack came back
/// is to ask. Prefer an event-driven wait wherever one genuinely exists; reach for
/// this only when it does not.
/// </remarks>
internal static class ReadinessPoll
{
    /// <summary>
    /// Polls <paramref name="isReady"/> every <paramref name="interval"/> until it
    /// returns <see langword="true"/>, returning how long that took — or
    /// <see langword="null"/> if <paramref name="timeout"/> elapsed first.
    /// </summary>
    /// <remarks>
    /// Checks before the first delay, so an already-ready subject costs one predicate
    /// call and no wall-clock. A predicate that throws is not caught: a probe that
    /// cannot answer is a real fault, not a "not ready yet".
    /// </remarks>
    /// <param name="isReady">
    /// The question being asked, as a pure predicate over observed state. Called
    /// before the first delay and once per interval thereafter. It is not allowed to
    /// throw: a probe that cannot answer is a real fault, not a "not ready yet", so
    /// the exception propagates rather than being read as a negative result.
    /// </param>
    /// <param name="timeout">
    /// How long to keep asking before giving up and returning <see langword="null"/>.
    /// Measured from entry, and checked after the predicate, so a zero or already
    /// expired timeout still reports a subject that is ready right now.
    /// </param>
    /// <param name="interval">
    /// How long to wait between predicate calls. The final wait is shortened to land
    /// on the deadline rather than overshoot it.
    /// </param>
    /// <param name="ct">
    /// Cancels the wait. Cancellation is a caller decision, not a timeout, so it
    /// surfaces as an <see cref="OperationCanceledException"/> instead of the
    /// <see langword="null"/> that means "not ready in time".
    /// </param>
    /// <param name="timeProvider">
    /// Clock for the deadline and the delay. Defaults to
    /// <see cref="TimeProvider.System"/>; a test passes one whose readings it
    /// controls, which is the only way to land on the deadline boundary on
    /// purpose rather than by luck (ADR-0052).
    /// </param>
    internal static async ValueTask<TimeSpan?> UntilAsync(
        Func<bool> isReady, TimeSpan timeout, TimeSpan interval, CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(isReady);

        var clock = timeProvider ?? TimeProvider.System;
        var start = clock.GetTimestamp();
        while (true)
        {
            if (isReady())
                return clock.GetElapsedTime(start);

            // One reading, used for both the deadline decision and the delay computed
            // from it. Two readings raced each other: the guard could pass on a value
            // just under the timeout, the clock cross the deadline before the
            // subtraction, and Task.Delay then receive a negative TimeSpan and throw
            // ArgumentOutOfRangeException — turning "not ready in time", which this
            // method reports by returning null, into an argument exception naming an
            // internal parameter, on the reset path where it is least welcome (#226).
            // A loaded machine widens that window; any preemption between the two
            // reads was enough to open it.
            //
            // Checked after the predicate so the deadline can never reject a subject
            // that is already ready — otherwise a zero/expired timeout would report a
            // failure the caller could see is untrue.
            var remaining = timeout - clock.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
                return null;

            // Never overshoot the deadline just to complete a whole interval.
            await Task.Delay(remaining < interval ? remaining : interval, clock, ct).ConfigureAwait(false);
        }
    }
}
