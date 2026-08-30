// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;

namespace Periphery.Treehopper.Flasher.Cli;

/// <summary>
/// The imperative shell around <see cref="BoardReboot"/>'s pure fold (ADR-0052): subscribe to the
/// OS device notifications for one board, perform an action that should reset it, and fold the
/// edges that follow into a <see cref="RebootObservation"/>.
/// </summary>
/// <remarks>
/// <para>
/// It owns exactly the things the core must not: the subscription, the clock, the lock, and the
/// wait. Every edge it sees becomes a value the fold decides on.
/// </para>
/// <para>
/// <b>Handlers outlive the unsubscribe.</b> Device events are raised straight onto thread-pool
/// threads, and disposing the watcher stops new dispatch without waiting for one already running.
/// Everything the shell owns is therefore serialized on one lock, including tearing the gate down.
/// </para>
/// <para>
/// Shared by the <c>reboot</c> and <c>rescue</c> verbs. They differ only in the action they
/// perform and in how the resulting observation is worded — the detection is identical, and was
/// worth extracting rather than copying: it is subtle in three separate ways (see below), and two
/// divergent copies of it is how the original 500 ms poll survived as long as it did.
/// </para>
/// </remarks>
internal static class BoardTransitionWatch
{
    /// <summary>
    /// Subscribes, runs <paramref name="act"/>, and folds the device edges that follow into an
    /// observation. Returns as soon as both edges are in, or when <paramref name="budget"/> expires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Subscribe first, act second.</b> The board is off the bus for roughly 230 ms, so a
    /// detector that starts after the reset — or that samples the device tree on any interval
    /// coarser than the transient — misses a reset that plainly happened. That is exactly how
    /// <c>reboot</c> came to report <c>NO EFFECT</c> for a working reboot (#230).
    /// </para>
    /// <para>
    /// One physical transition can arrive as two events (deactivate + remove, or appear +
    /// activate), and the watcher's initial snapshot activates the board that is still present.
    /// All three are absorbed by the fold rather than special-cased here.
    /// </para>
    /// <para>
    /// <paramref name="act"/> runs with the clock already started, so every reported time is
    /// relative to it. Whatever it throws propagates to the caller — the caller knows how to word
    /// its own failure, and cancellation must never be folded into a device verdict.
    /// </para>
    /// </remarks>
    /// <param name="info">The board to watch. Identity is matched by <see cref="BoardReboot.IsSameBoard"/>.</param>
    /// <param name="act">The reset to perform, once the subscription is live.</param>
    /// <param name="budget">How long to wait for both edges before giving up.</param>
    /// <param name="ct">Cancels the subscription, the action, and the wait.</param>
    public static async Task<RebootObservation> WatchAsync(
        DeviceInfo info, Func<CancellationToken, Task> act, TimeSpan budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(act);

        var observed = new RebootObservation();
        var clock = new Stopwatch();
        var sync = new object();

        // The gate's maximum of 1 is load-bearing — a second Release() would throw — and it holds
        // because the fold reaches IsComplete exactly once: the only transition that sets ReturnedAt
        // requires it to be null.
        //
        // Not a `using`, and `closed` is not redundant with disposal. Unsubscribing a handler stops
        // NEW dispatch; it does not wait for one already running on a thread-pool thread. So a
        // disposed gate is still reachable from an in-flight Record — narrowly, but a Release() on a
        // disposed semaphore throws out of the handler and into the watcher's dispatch. Closing the
        // gate under the SAME lock Record takes is what actually rules that out: after the lock is
        // released nothing can enter the body again, and anything already inside has finished.
        var gate = new SemaphoreSlim(0, 1);
        bool closed = false;

        // No DeviceInfo parameter: the watcher's filter is the identity check, so an edge that
        // reaches here is this board's by construction and there is nothing left to inspect.
        void Record(RebootSignalKind kind)
        {
            // Events arrive on thread-pool threads, so the fold runs under a lock. It stays a fold:
            // the lock guards the two fields the shell owns, not any decision.
            lock (sync)
            {
                if (closed) return;
                var next = observed.Observe(new RebootSignal(kind, clock.Elapsed));
                if (next == observed) return;
                observed = next;
                if (next.IsComplete) gate.Release();
            }
        }

        void OnGone(object? _, DeviceChangeEventArgs e) => Record(RebootSignalKind.Gone);
        void OnBack(object? _, DeviceChangeEventArgs e) => Record(RebootSignalKind.Back);

        try
        {
            // The filter is the identity match, so only this board's edges reach the fold. It is
            // case-insensitive throughout: an instance id, and the serial inside it, re-enumerate with
            // different casing (#231). The watcher evaluates it per event and per snapshot entry, so it
            // does run for every device on the box — it is a length check and one string compare, and
            // the watcher lives only for the ~0.25s of one reset, so that cost is not worth avoiding.
            //
            // Disposed at the end of this block — so on every path out, including a throw from act,
            // the unsubscribe happens before the finally below closes the gate.
            await using var watcher = Devices.Watch().Where(d => BoardReboot.IsSameBoard(d, info));
            watcher.Deactivated += OnGone;
            watcher.Disappeared += OnGone;
            watcher.Appeared += OnBack;
            watcher.Activated += OnBack;
            await watcher.StartAsync(ct);

            // The clock's zero is the reset, which is what every reported time is relative to. It reads
            // zero until here, so an edge arriving earlier would stamp 0 ms — but the board cannot leave
            // the bus before it is told to, and if it left for any other reason the action throws and
            // this method never returns an observation at all.
            clock.Start();
            await act(ct);

            // Returns as soon as both edges are in; the budget only bounds the failure cases.
            await gate.WaitAsync(budget, ct);

            lock (sync) return observed;
        }
        finally
        {
            lock (sync) closed = true;
            gate.Dispose();
        }
    }
}
