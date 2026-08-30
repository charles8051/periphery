// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery;

/// <summary>
/// Pure classification of a device's <em>conceivable</em> reset strategies from
/// the transport markers carried on its <see cref="DeviceInfo"/> snapshot — no
/// device tree walk, no IO (ADR-0060 Decision 2, static half). The runtime half
/// (is the parent hub/port actually resolvable? does it support power switching?)
/// lives in the platform <see cref="IDeviceReset"/> shell, which calls this for
/// the baseline set and then refines it.
/// </summary>
/// <remarks>
/// Kept pure and platform-agnostic so it is exhaustively unit-testable with
/// hand-built <see cref="DeviceInfo"/> values and no hardware.
/// </remarks>
public static class ResetStrategyMap
{
    // Built once: USB-backed devices advertise a port-cycle (re-enumerates) then
    // a PnP disable/enable fallback (does not), gentlest hard-strategy first.
    // A device extension may prepend a SoftProtocol strategy; the platform shell
    // may refine the blast radius once it has resolved the hub topology.
    //
    // The disable/enable rung's ReEnumerates: false is hardware-measured, not inferred
    // (periphery #251) — read ResetKind.PnpDisableEnable before changing it, including
    // why flipping it to true is a plausible-looking fix that buys nothing.
    private static readonly ResetStrategy[] s_usb =
    [
        new ResetStrategy(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true),
        new ResetStrategy(ResetKind.PnpDisableEnable, ResetBlastRadius.Self, ReEnumerates: false),
    ];

    private static readonly ResetStrategy[] s_none = [];

    /// <summary>
    /// The conceivable reset strategies for <paramref name="device"/> based on
    /// transport alone, gentlest first. Empty for non-resettable transports
    /// (PS/2, virtual/software, network, PCI, Bluetooth, bare HID with no known
    /// USB backing). The shell adds USB strategies for a bridged device once it
    /// has walked to a USB ancestor.
    /// </summary>
    public static IReadOnlyList<ResetStrategy> ForTransport(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsUsbBacked(device) ? s_usb : s_none;
    }

    /// <summary>
    /// The USB reset strategy set (port-cycle then disable/enable), gentlest
    /// first. Exposed so the platform shell can advertise it for a bridged
    /// device it resolved to a USB ancestor, without duplicating the table.
    /// </summary>
    public static IReadOnlyList<ResetStrategy> UsbStrategies => s_usb;

    /// <summary>
    /// <see langword="true"/> when the device's own snapshot marks it as
    /// USB-attached. Cross-platform: Windows reports <see cref="BusType.USB"/>
    /// and a <c>USB\</c> instance id; Linux reports the <c>usb</c> udev
    /// subsystem; macOS reports an <c>IOUSB*</c> service class. A bridged child
    /// (a USB-serial COM node, a HID-as-COM scanner) may not match here — that is
    /// the ancestor-walk case the platform shell handles.
    /// </summary>
    public static bool IsUsbBacked(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.BusType == BusType.USB
            || string.Equals(device.Subsystem, "usb", StringComparison.OrdinalIgnoreCase)
            || (device.IOServiceClass is { } ios && ios.StartsWith("IOUSB", StringComparison.OrdinalIgnoreCase))
            || device.Id.Value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
    }
}
