// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// Immutable snapshot of a <see cref="DeviceTracker"/>'s resolved state.
/// Carried by both <see cref="DeviceTracker.StateChanged"/> and
/// <see cref="IObservable{T}"/> subscriptions, so both surfaces deliver
/// the same atomic snapshot with no need to re-read from the tracker.
/// </summary>
public sealed record DeviceTrackerState(
    DeviceInfo? Device,
    DeviceActivityStatus ActivityStatus,
    DeviceProfile? ActiveProfile)
{
    /// <summary><c>true</c> when <see cref="ActivityStatus"/> is <see cref="DeviceActivityStatus.Active"/>.</summary>
    public bool IsActive => ActivityStatus == DeviceActivityStatus.Active;

    /// <summary><c>true</c> when <see cref="Device"/> is non-null.</summary>
    public bool IsPresent => Device is not null;

    /// <summary>
    /// <c>false</c> when the tracker has no profiles, so it matches nothing and
    /// stays <see cref="DeviceActivityStatus.Absent"/> until profiles are
    /// assigned with <see cref="DeviceTracker.ReplaceProfiles"/>. Separates an
    /// unassigned tracker from one whose assigned device is absent. Defaults to
    /// <c>true</c>.
    /// </summary>
    public bool IsConfigured { get; init; } = true;
}
