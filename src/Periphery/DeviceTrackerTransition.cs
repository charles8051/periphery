// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// An atomic snapshot of a <see cref="DeviceTracker"/> state transition,
/// delivered as the event argument for the tracker's edge events
/// (<see cref="DeviceTracker.Appeared"/>, <see cref="DeviceTracker.Disappeared"/>,
/// <see cref="DeviceTracker.Activated"/>, <see cref="DeviceTracker.Deactivated"/>).
/// </summary>
/// <remarks>
/// Both snapshots are captured under the tracker's internal lock before any
/// notification fires — they are always mutually consistent.
/// <para><see cref="Before"/> is the primary value on <see cref="DeviceTracker.Disappeared"/>
/// and <see cref="DeviceTracker.Deactivated"/> — <c>After.Device</c> is <c>null</c> or
/// downgraded at that point, so <c>Before.Device</c> is the last place the previous
/// snapshot lives without the consumer caching it themselves.</para>
/// </remarks>
/// <param name="Before">The tracker state immediately before the transition.</param>
/// <param name="After">The tracker state immediately after the transition.</param>
public readonly record struct DeviceTrackerTransition(
    DeviceTrackerState Before,
    DeviceTrackerState After);
