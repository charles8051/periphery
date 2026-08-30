// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Event args carrying a <see cref="DeviceInfo"/> payload.
/// </summary>
public sealed class DeviceChangeEventArgs : EventArgs
{
    /// <summary>The device that triggered the event.</summary>
    public DeviceInfo Device { get; }

    public DeviceChangeEventArgs(DeviceInfo device) => Device = device;
}
