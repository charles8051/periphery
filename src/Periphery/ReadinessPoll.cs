// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics;
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
    internal static async ValueTask<TimeSpan?> UntilAsync(
        Func<bool> isReady, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isReady);

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (isReady())
                return elapsed.Elapsed;

            // Checked after the predicate so the deadline can never reject a subject
            // that is already ready — otherwise a zero/expired timeout would report a
            // failure the caller could see is untrue.
            if (elapsed.Elapsed >= timeout)
                return null;

            // Never overshoot the deadline just to complete a whole interval.
            var remaining = timeout - elapsed.Elapsed;
            await Task.Delay(remaining < interval ? remaining : interval, ct).ConfigureAwait(false);
        }
    }
}
