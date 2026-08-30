// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery;

/// <summary>
/// Provides the previous and current <see cref="DeviceInfo"/> snapshots when
/// one or more properties on a connected device change value, along with the
/// set of property names that differ between the two snapshots.
/// </summary>
/// <remarks>
/// <para>Property names in <see cref="ChangedProperties"/> match the C# property
/// names on <see cref="DeviceInfo"/> (e.g. <c>"BatteryChargePercent"</c>).
/// Use <c>nameof(DeviceInfo.BatteryChargePercent)</c> for safe comparisons.</para>
/// <para>The raw OS properties bag (<c>DeviceInfo.Properties</c>) is excluded
/// from the diff — only typed first-class properties are reported.</para>
/// </remarks>
public sealed class DevicePropertyChangedEventArgs : EventArgs
{
    /// <summary>The device snapshot before the change.</summary>
    public DeviceInfo Previous { get; }

    /// <summary>The device snapshot after the change.</summary>
    public DeviceInfo Current { get; }

    /// <summary>
    /// The names of properties that changed between <see cref="Previous"/>
    /// and <see cref="Current"/>.
    /// </summary>
    public IReadOnlySet<string> ChangedProperties { get; }

    internal DevicePropertyChangedEventArgs(
        DeviceInfo previous,
        DeviceInfo current,
        IReadOnlySet<string> changedProperties)
    {
        Previous = previous;
        Current = current;
        ChangedProperties = changedProperties;
    }
}
