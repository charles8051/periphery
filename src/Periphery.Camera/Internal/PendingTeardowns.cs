// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

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
/// <see cref="CameraTeardownPendingException"/> until the work completes.
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
/// <b>A refusal expires after <see cref="RefusalWindow"/>, and the entry does
/// not.</b> The premise the earlier bounded window was reverted over is now
/// measured (issue #221 item 1): on a re-enumerating UVC camera the parked calls
/// were still parked 14 hours later, and the process was refused across 6,989
/// consecutive opens before an unrelated restart cleared it. So a permanent
/// refusal is not a conservative failure — it is an outage that no caller can
/// end, on a device every other process on the machine can open.
/// </para>
/// <para>
/// The objection that reverted it was real and is answered by shape rather than
/// by argument. Past the window a proxy on the default backoff retries forever,
/// and each retry is a real native open against a driver that may still be
/// wedged. But the measured alternative is the same caller retrying forever
/// against a refusal that provably cannot lift, so the choice is between retries
/// that can eventually succeed and retries that cannot. What the window must not
/// do is destroy the diagnostic, which is the part of #123 that actually cost the
/// time: so expiry stops the <em>refusal</em> and keeps the <em>record</em>. The
/// entry stays until its work completes, <see cref="WhenClearedAsync"/> still
/// resolves against it, and each open that proceeds over an expired entry
/// increments <see cref="CameraDiagnostics.TeardownRefusalsExpired"/> and logs at
/// Warning naming the steps and how long they have been parked. A failure after
/// that reads as "opened over an abandoned teardown" rather than as a fresh
/// format negotiation failure.
/// </para>
/// <para>
/// The window length is a policy choice, not a derived number, and is documented
/// as one on <see cref="RefusalWindow"/>. What would let it be derived is a bench
/// measurement on a genuinely wedged driver; the histogram
/// <see cref="CameraDiagnostics.AbandonedTeardownDuration"/> collects the
/// distribution for the steps that do come back.
/// </para>
/// <para>
/// This registry still never sees PnP edges, and observing them would not replace
/// the window. Windows commonly preserves the device instance id across a
/// re-enumeration, so the returning device hashes to the same key; and the
/// provider's stale-removal guard deliberately drops a removal whose instance is
/// started again by the time it is processed, which is exactly the fast bounce
/// that produced the measurement above.
/// </para>
/// </remarks>
internal static partial class PendingTeardowns
{
    private static readonly ConcurrentDictionary<string, PendingTeardown> s_pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a device stays refused after its first teardown step was
    /// abandoned. Past this the open is allowed through, and the record is kept.
    /// </summary>
    /// <remarks>
    /// A policy choice rather than a derived number, and deliberately far above
    /// the budgets it backstops: the longest is
    /// <see cref="BoundedTeardown.BackendDisposeBudget"/> at 3 seconds, so a step
    /// that reaches this one has been parked for twenty times the budget it
    /// overran. That gap is the point. The window exists to end a permanent
    /// lockout, not to hurry a teardown that is merely slow, and a driver call
    /// that has not returned in a minute is not going to be rescued by refusing
    /// one more open. Deriving this properly needs the duration distribution for
    /// steps that do eventually come back, which
    /// <see cref="CameraDiagnostics.AbandonedTeardownDuration"/> records.
    /// </remarks>
    internal static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(60);

    /// <summary>Devices with at least one abandoned teardown step still running.</summary>
    internal static int Count => s_pending.Count;

    /// <summary>
    /// Records <paramref name="work"/> as an abandoned teardown step on
    /// <paramref name="deviceId"/>. A device that already has one pending gains a
    /// second step; the device clears when every registered step has completed.
    /// </summary>
    internal static PendingTeardown Register(string deviceId, string step, Task work, TimeProvider clock)
    {
        var settled = Settle(work);
        var entry = s_pending.AddOrUpdate(
            deviceId,
            _ => new PendingTeardown(deviceId, [step], settled, clock.GetTimestamp(), clock),
            (_, existing) => existing.With(step, settled));

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

    /// <summary>The pending teardown on <paramref name="deviceId"/>, or null when none is.</summary>
    internal static PendingTeardown? Find(string deviceId) =>
        s_pending.TryGetValue(deviceId, out var entry) && !entry.Completion.IsCompleted ? entry : null;

    /// <summary>
    /// Refuses an open on a device whose previous teardown has not finished, for
    /// up to <see cref="RefusalWindow"/> after the first step was abandoned.
    /// </summary>
    /// <remarks>
    /// The recheck form. Each open path calls this again after its native work,
    /// so it stays silent when it admits: <see cref="AdmitOrThrow"/> has already
    /// counted and warned for this open, and counting here as well would report
    /// three admissions for one.
    /// </remarks>
    /// <exception cref="CameraTeardownPendingException">
    /// An abandoned teardown step on <paramref name="deviceId"/> is still running,
    /// and was abandoned less than <see cref="RefusalWindow"/> ago.
    /// </exception>
    internal static void ThrowIfPending(string deviceId)
    {
        if (Find(deviceId) is { } pending && pending.PendingFor < RefusalWindow)
            throw pending.ToException();
    }

    /// <summary>
    /// The same refusal at the top of an open path, recorded when it admits.
    /// </summary>
    /// <remarks>
    /// Admitting past the window is the interesting event, not the refusal: it is
    /// a real native open against a device a background thread may still hold. One
    /// counter increment and one Warning per open carry that, so a failure
    /// downstream reads as "opened over an abandoned teardown" rather than as a
    /// camera that cannot produce the format, which is how the #123 cascade was
    /// misread for nineteen consecutive failures.
    /// </remarks>
    /// <param name="deviceId">The device about to be opened.</param>
    /// <param name="logger">The opening caller's logger.</param>
    /// <exception cref="CameraTeardownPendingException">
    /// An abandoned teardown step on <paramref name="deviceId"/> is still running,
    /// and was abandoned less than <see cref="RefusalWindow"/> ago.
    /// </exception>
    internal static void AdmitOrThrow(string deviceId, ILogger logger)
    {
        if (Find(deviceId) is not { } pending)
            return;

        var pendingFor = pending.PendingFor;
        if (pendingFor < RefusalWindow)
            throw pending.ToException();

        CameraDiagnostics.TeardownRefusalsExpired.Add(1);
        LogRefusalExpired(
            logger,
            deviceId,
            string.Join(", ", pending.Steps),
            pendingFor.TotalSeconds,
            RefusalWindow.TotalSeconds);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Opening camera {DeviceId} over an abandoned teardown ({Steps}) that has been "
            + "running for {PendingSec:F1}s, past the {WindowSec:F0}s refusal window. The thread may "
            + "still hold the device, so this open can fail or return a faulting session; a failure "
            + "here is not evidence about the camera's formats or capabilities.")]
    private static partial void LogRefusalExpired(
        ILogger logger, string deviceId, string steps, double pendingSec, double windowSec);

    /// <summary>
    /// Completes when the device has no abandoned teardown step still running.
    /// </summary>
    /// <remarks>
    /// Re-reads the registry each loop rather than awaiting a single snapshot,
    /// so a step registered after the one being awaited is picked up too. A
    /// second step can register while the first is in flight — stop abandoned,
    /// then producer-exit abandoned — and each abandonment replaces the entry
    /// with a wider composite. Awaiting the entry captured at one instant would
    /// return while a later step is still running; this does not.
    /// </remarks>
    internal static async Task WhenClearedAsync(string deviceId)
    {
        while (Find(deviceId) is { } pending)
            await pending.Completion.ConfigureAwait(false);
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

    internal PendingTeardown With(string step, Task settledWork) =>
        new(DeviceId, [.. Steps, step], Task.WhenAll(Completion, settledWork), _firstAbandonedAt, _clock);

    internal CameraTeardownPendingException ToException()
    {
        var pendingFor = PendingFor;
        // The exception's Completion is resolved against the registry, not from
        // this entry's snapshot: a step abandoned after this instance was read
        // replaces the entry with a wider composite, and a caller awaiting the
        // signal must wait for that step too (issue #123 review).
        // No replug advice. The registration is keyed by device id, and both
        // Windows and Linux commonly hand the same id back across a
        // re-enumeration, so the returning device hashes to this same entry and is
        // refused on arrival: the message would be naming the one action that
        // cannot help (issue #221 item 1). What does lift it is the work
        // completing, or the refusal window expiring.
        var remaining = PendingTeardowns.RefusalWindow - pendingFor;
        return new CameraTeardownPendingException(
            $"Camera '{DeviceId}' cannot be opened: a previous session's teardown " +
            $"({string.Join(", ", Steps)}) overran its budget {pendingFor.TotalSeconds:F1}s ago and is " +
            "still running in the background, holding the device. Await Completion and retry. " +
            $"Failing that, the refusal lifts on its own in {remaining.TotalSeconds:F0}s and the open " +
            "is allowed through. Replugging the camera does not clear it: the registration is keyed " +
            "by device id, which the OS commonly preserves across a re-enumeration.",
            DeviceId, PendingTeardowns.WhenClearedAsync(DeviceId), pendingFor);
    }
}
