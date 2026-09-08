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
/// </remarks>
internal static class PendingTeardowns
{
    private static readonly ConcurrentDictionary<string, PendingTeardown> s_pending =
        new(StringComparer.OrdinalIgnoreCase);

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
    /// Refuses an open on a device whose previous teardown has not finished.
    /// </summary>
    /// <exception cref="CameraTeardownPendingException">
    /// An abandoned teardown step on <paramref name="deviceId"/> is still running.
    /// </exception>
    internal static void ThrowIfPending(string deviceId)
    {
        if (Find(deviceId) is { } pending)
            throw pending.ToException();
    }

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
        return new CameraTeardownPendingException(
            $"Camera '{DeviceId}' cannot be opened: a previous session's teardown " +
            $"({string.Join(", ", Steps)}) overran its budget {pendingFor.TotalSeconds:F1}s ago and is " +
            "still running in the background, holding the device. Await Completion and retry, or " +
            "replug the camera if the driver is wedged.",
            DeviceId, PendingTeardowns.WhenClearedAsync(DeviceId), pendingFor);
    }
}
