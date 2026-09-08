// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;

namespace Periphery.Camera.Internal;

/// <summary>
/// Process-wide record of camera teardown work that overran its budget and was
/// left running on a background thread while still holding the device
/// (issue #123).
/// </summary>
/// <remarks>
/// <para>
/// A bounded teardown step (<see cref="BoundedTeardown"/>) that overruns is
/// abandoned, not cancelled: the thread is still inside a driver call, and that
/// call may hold the device's COM source, file descriptor, or buffer queue. The
/// next open of the same device contends with it. On a wedged Media Foundation
/// driver, one abandoned teardown cascaded into nineteen consecutive failures on
/// a C270, each of which looked like the camera could not produce the format.
/// So an abandoned step is registered here against its device id, and every
/// open path (<see cref="CameraDevice.OpenAsync(DeviceInfo, CancellationToken, Microsoft.Extensions.Logging.ILogger{CameraDevice}?)"/>,
/// <see cref="CameraDevice.ReadSnapshotAsync(DeviceInfo, System.Threading.CancellationToken)"/>,
/// <see cref="CameraDevice.OpenSessionAsync"/>) refuses the device with
/// <see cref="CameraTeardownPendingException"/> until the work completes or the
/// refusal lapses (<see cref="RefusalTtl"/>).
/// </para>
/// <para>
/// Keyed by <see cref="DeviceInfo.Id"/>, compared
/// <see cref="StringComparer.OrdinalIgnoreCase"/>. Windows presents the same
/// device instance id in different cases depending on which API produced it,
/// and a miss here reopens into exactly the contention this registry exists to
/// prevent.
/// </para>
/// <para>
/// Static by necessity. The abandoned thread is a process-level fact with no
/// owner left alive to hold it: the session and device that started the
/// teardown are disposed by the time it matters.
/// </para>
/// <para>
/// The guarantee is exact for the case this exists to cover. Abandonment
/// registers <em>before</em> the disposing session's <c>DisposeAsync</c>
/// returns, so a consumer that disposes one session and then opens another on
/// the same device — the long-lived reopen-after-fault pattern, and the #123
/// cascade itself — always sees the registration and is refused. An open that
/// <em>overlaps</em> a still-running dispose is narrower: each open path
/// rechecks after its last device access, so an abandonment that registers
/// while the open is doing native work is caught before a usable handle is
/// returned, but the check is a read, not a reservation.
/// </para>
/// <para>
/// <b>There is no per-device lease, and that is now a decision rather than a
/// deferral.</b> The reservation was left to <c>CameraDeviceProxy</c> when it
/// landed (ADR-0084 D5); the proxy has landed and does not take it, for three
/// reasons. A proxy-owned lease covers only proxy-driven opens, and
/// <c>CameraSession.For(DeviceInfo)</c> remains supported, so it would buy
/// partial atomicity in place of a stated boundary. A registry-owned lease
/// fares no better on the case that matters: the abandoned thread is already
/// inside a driver call, so the contention it causes is at the OS and no C#
/// mutual exclusion reaches it. And the residual window it would close — an
/// abandonment registering between the recheck and the handle being returned —
/// hands the caller a session that faults on first use, which the recovery
/// ladder already treats as an ordinary stream fault. The lease would convert
/// one recoverable fault into another.
/// </para>
/// <para>
/// <b>A refusal expires.</b> An abandoned step that never returns would
/// otherwise refuse the device for the life of the process, which makes the
/// exception's own remedy — replug the camera — inert, because a replug
/// produces PnP edges this registry never sees. So <see cref="RefusalTtl"/>
/// bounds the refusal: past it, <see cref="Find"/> reports nothing and the open
/// is attempted. If the driver is genuinely still held, that open fails and the
/// caller's recovery ladder handles it, which is the pre-#123 behaviour for one
/// device rather than a process-lifetime outage. The asymmetry is deliberate:
/// expiring too early degrades into a noisy ladder, expiring too late has no
/// recovery at all.
/// </para>
/// </remarks>
internal static class PendingTeardowns
{
    private static readonly ConcurrentDictionary<string, PendingTeardown> s_pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long an abandoned teardown refuses opens of its device before the
    /// refusal lapses and the open is attempted anyway.
    /// </summary>
    /// <remarks>
    /// A minute, against teardown budgets of two, two and three seconds. A step
    /// that is going to return returns in seconds; one still parked a minute
    /// later is not completing on its own, and by then the operator has had time
    /// to do the thing the exception message asks for. The window is not
    /// configurable — a knob here would be tuned to zero, which is the
    /// pre-#123 cascade.
    /// </remarks>
    internal static readonly TimeSpan RefusalTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Devices with at least one abandoned teardown step still running, whether
    /// or not it is still refusing opens. A lapsed entry stays counted here until
    /// its steps complete.
    /// </summary>
    internal static int Count => s_pending.Count;

    /// <summary>
    /// Records <paramref name="work"/> as an abandoned teardown step on
    /// <paramref name="deviceId"/>. A device that already has one pending gains a
    /// second step; the device clears when every registered step has completed.
    /// </summary>
    /// <remarks>
    /// An existing entry whose refusal has already lapsed is replaced rather than
    /// widened. Composing onto it would carry its first-abandoned timestamp
    /// forward, so the new step would be born expired and refuse nothing.
    /// </remarks>
    internal static PendingTeardown Register(string deviceId, string step, Task work, TimeProvider clock)
    {
        var settled = Settle(work);
        var entry = s_pending.AddOrUpdate(
            deviceId,
            _ => new PendingTeardown(deviceId, [step], settled, clock.GetTimestamp(), clock),
            (_, existing) => existing.HasLapsed
                ? new PendingTeardown(deviceId, [step], settled, clock.GetTimestamp(), clock)
                : existing.With(step, settled));

        // Remove the entry once its composite completes, and only if it is still
        // the entry registered here. A later Register on the same device replaces
        // the value with a wider composite, which removes itself in turn; this
        // continuation then finds a different value and leaves it alone.
        entry.Completion.ContinueWith(
            static (_, state) =>
            {
                var (id, e) = ((string, PendingTeardown))state!;
                s_pending.TryRemove(new KeyValuePair<string, PendingTeardown>(id, e));
            },
            (deviceId, entry),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return entry;
    }

    /// <summary>
    /// The pending teardown on <paramref name="deviceId"/>, or null when none is
    /// — including when one is still running but its refusal has lapsed past
    /// <see cref="RefusalTtl"/>.
    /// </summary>
    /// <remarks>
    /// A lapsed entry is left in the dictionary rather than removed here. Its own
    /// completion continuation is what removes it, and that continuation only
    /// removes the value it registered, so tearing it out from a read path would
    /// race a concurrent <see cref="Register"/> for no benefit.
    /// </remarks>
    internal static PendingTeardown? Find(string deviceId) =>
        s_pending.TryGetValue(deviceId, out var entry)
        && !entry.Completion.IsCompleted
        && !entry.HasLapsed
            ? entry
            : null;

    /// <summary>
    /// Refuses an open on a device whose previous teardown has not finished, for
    /// as long as <see cref="RefusalTtl"/> from the first overrun.
    /// </summary>
    /// <exception cref="CameraTeardownPendingException">
    /// An abandoned teardown step on <paramref name="deviceId"/> is still running
    /// and its refusal has not lapsed.
    /// </exception>
    internal static void ThrowIfPending(string deviceId)
    {
        if (Find(deviceId) is { } pending)
            throw pending.ToException();
    }

    /// <summary>
    /// Completes when the device has no abandoned teardown step still running,
    /// or when the refusal lapses past <see cref="RefusalTtl"/> — whichever
    /// comes first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-reads the registry each loop rather than awaiting a single snapshot,
    /// so a step registered after the one being awaited is picked up too. A
    /// second step can register while the first is in flight — stop abandoned,
    /// then producer-exit abandoned — and each abandonment replaces the entry
    /// with a wider composite. Awaiting the entry captured at one instant would
    /// return while a later step is still running; this does not.
    /// </para>
    /// <para>
    /// The wait is bounded by the same window that bounds the refusal. Without
    /// that, the exception's advice to await <c>Completion</c> and retry is an
    /// instruction to wait forever on a step that never returns, which is the
    /// hang this registry was built to replace rather than reproduce.
    /// </para>
    /// </remarks>
    internal static async Task WhenClearedAsync(string deviceId)
    {
        while (Find(deviceId) is { } pending)
        {
            var remaining = RefusalTtl - pending.PendingFor;
            if (remaining <= TimeSpan.Zero)
                return;

            await pending.WaitAtMostAsync(remaining).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Test seam: forgets every registration. The abandoned work itself keeps
    /// running; only the record of it is dropped.
    /// </summary>
    internal static void Clear() => s_pending.Clear();

    /// <summary>
    /// A task that completes when <paramref name="work"/> does, successfully
    /// whatever the outcome. Reading the fault is what marks it observed, so an
    /// abandoned step that eventually throws never surfaces as an unobserved
    /// task exception.
    /// </summary>
    private static Task Settle(Task work) =>
        work.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

/// <summary>
/// One device's abandoned teardown: which steps overran, when the first one did,
/// and a task that completes when all of them have finished.
/// </summary>
internal sealed class PendingTeardown
{
    private readonly long _firstAbandonedAt;
    private readonly TimeProvider _clock;

    internal PendingTeardown(
        string deviceId, IReadOnlyList<string> steps, Task completion, long firstAbandonedAt, TimeProvider clock)
    {
        DeviceId = deviceId;
        Steps = steps;
        Completion = completion;
        _firstAbandonedAt = firstAbandonedAt;
        _clock = clock;
    }

    internal string DeviceId { get; }

    /// <summary>The steps that overran, in the order they were abandoned.</summary>
    internal IReadOnlyList<string> Steps { get; }

    /// <summary>Completes, always successfully, once every abandoned step has finished.</summary>
    internal Task Completion { get; }

    /// <summary>Time since the first step on this device was abandoned, on the clock that abandoned it.</summary>
    internal TimeSpan PendingFor => _clock.GetElapsedTime(_firstAbandonedAt);

    /// <summary>
    /// True once this entry has been pending longer than
    /// <see cref="PendingTeardowns.RefusalTtl"/>. The steps may still be running;
    /// this says only that they have stopped refusing opens.
    /// </summary>
    internal bool HasLapsed => PendingFor >= PendingTeardowns.RefusalTtl;

    /// <summary>
    /// Completes when every step has finished or <paramref name="remaining"/>
    /// elapses on this entry's own clock, whichever is first. Never throws.
    /// </summary>
    internal async Task WaitAtMostAsync(TimeSpan remaining)
    {
        // Cancel the delay when the work wins, so its timer does not outlive the
        // wait — the same discipline BoundedTeardown applies to its budgets.
        using var expiry = new CancellationTokenSource();
        var winner = await Task.WhenAny(Completion, Task.Delay(remaining, _clock, expiry.Token))
            .ConfigureAwait(false);
        if (winner == Completion)
            expiry.Cancel();
    }

    internal PendingTeardown With(string step, Task settledWork) =>
        new(DeviceId, [.. Steps, step], Task.WhenAll(Completion, settledWork), _firstAbandonedAt, _clock);

    internal CameraTeardownPendingException ToException()
    {
        var pendingFor = PendingFor;
        // The exception's Completion is resolved against the registry, not from
        // this entry's snapshot: a step abandoned after this instance was read
        // replaces the entry with a wider composite, and a caller awaiting the
        // signal must wait for that step too (issue #123 review).
        return new CameraTeardownPendingException(
            $"Camera '{DeviceId}' cannot be opened: a previous session's teardown " +
            $"({string.Join(", ", Steps)}) overran its budget {pendingFor.TotalSeconds:F1}s ago and is " +
            "still running in the background, holding the device. Await Completion and retry, or " +
            "replug the camera if the driver is wedged. Opens are refused for at most " +
            $"{PendingTeardowns.RefusalTtl.TotalSeconds:F0}s from the first overrun; after that they " +
            "are attempted again whether or not the teardown has returned.",
            DeviceId, PendingTeardowns.WhenClearedAsync(DeviceId), pendingFor);
    }
}
