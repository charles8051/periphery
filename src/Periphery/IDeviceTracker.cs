// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// Minimal observable surface for device-presence trackers. Implemented by
/// <see cref="DeviceTracker"/>; consumers can take this interface to enable
/// mocking in tests without standing up a real <see cref="DeviceWatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// The interface exposes the read-only query surface plus the
/// <see cref="StateChanged"/> event. Mutation (binding to a watcher,
/// receiving OS callbacks) stays on the concrete <see cref="DeviceTracker"/>
/// — fakes in tests just flip <see cref="IsActive"/> and raise
/// <see cref="StateChanged"/> directly.
/// </para>
/// <para>
/// For richer Rx-style observation, cast to <see cref="IObservable{T}"/>
/// of <see cref="DeviceTrackerState"/> — <see cref="DeviceTracker"/>
/// implements that too, but kept off this interface to keep the testing
/// shim minimal.
/// </para>
/// </remarks>
public interface IDeviceTracker
{
    /// <summary>Optional human-readable label for diagnostics / config keys.</summary>
    string? Name { get; }

    /// <summary>
    /// The best-known device snapshot — non-null whenever a matching device
    /// is <see cref="DeviceActivityStatus.Present"/> or
    /// <see cref="DeviceActivityStatus.Active"/>. Inspect
    /// <see cref="ActivityStatus"/> to distinguish.
    /// </summary>
    DeviceInfo? Device { get; }

    /// <summary>The resolved activity status of the tracked device.</summary>
    DeviceActivityStatus ActivityStatus { get; }

    /// <summary>
    /// <c>true</c> when <see cref="ActivityStatus"/> is
    /// <see cref="DeviceActivityStatus.Active"/>.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// <c>true</c> when <see cref="Device"/> is non-null — a matching device
    /// is known to the OS (paired, installed, or plugged in).
    /// </summary>
    bool IsPresent { get; }

    /// <summary>
    /// Raised when <see cref="Device"/>, <see cref="ActivityStatus"/>, or
    /// related resolved state changes. The argument is an atomic snapshot
    /// captured at the moment of the transition — no need to re-read.
    /// </summary>
    event EventHandler<DeviceTrackerState>? StateChanged;

    /// <summary>
    /// The atomic snapshot of the tracker's current state. Equivalent to
    /// the value <see cref="StateChanged"/> would deliver right now.
    /// </summary>
    DeviceTrackerState CurrentState { get; }
}
