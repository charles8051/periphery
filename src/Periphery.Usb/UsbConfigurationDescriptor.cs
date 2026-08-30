// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Usb;

/// <summary>
/// Immutable snapshot of a USB configuration descriptor and its interfaces.
/// </summary>
/// <remarks>
/// This spike's WinUSB backend surfaces the active (first) interface and its
/// pipes via the WinUSB query APIs. Full multi-interface / multi-alt-setting
/// parsing from the raw configuration-descriptor blob is a follow-up.
/// </remarks>
public sealed record UsbConfigurationDescriptor
{
    /// <summary>The configuration's <c>bConfigurationValue</c> (the value passed to SET_CONFIGURATION).</summary>
    public required byte ConfigurationValue { get; init; }

    /// <summary>Maximum bus power the configuration draws, in milliamps.</summary>
    public required int MaxPowerMilliamps { get; init; }

    /// <summary>The interfaces exposed by this configuration.</summary>
    public ImmutableArray<UsbInterfaceDescriptor> Interfaces { get; init; } =
        ImmutableArray<UsbInterfaceDescriptor>.Empty;
}
