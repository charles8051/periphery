// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Hid.Codecs;

/// <summary>
/// Battery state read by an <see cref="IHidUpsCodec"/> from a HID-class
/// power device. Returned by <see cref="HidBattery.ReadSnapshotAsync"/>
/// (the ADR-0026 Option D static helper); <see cref="DeviceInfo"/>
/// itself is intentionally NOT modified by snapshot reads — it remains
/// a pure zero-I/O enumeration artifact.
/// </summary>
/// <param name="BatteryChargePercent">
/// Estimated charge level (0–100), or <c>null</c> if the codec couldn't
/// derive one from the device's response. The Megatec Qx dialects do not
/// report a percent directly; the codec estimates it from battery voltage
/// with a generic discharge curve. Treat as approximate; some firmware
/// revisions expose a more precise reading via extended queries like
/// <c>QGS</c> that codecs may use when available.
/// </param>
/// <param name="BatteryStatus">
/// Charging / discharging / not-charging / full / critical / unknown, as
/// derived from the device's status flags (Megatec: bit 7 = utility fail,
/// bit 6 = battery low, etc.). <c>null</c> when the codec couldn't classify.
/// </param>
/// <param name="IsExternalPowerConnected">
/// <c>true</c> when AC line power is present (UPS is in pass-through /
/// charging mode), <c>false</c> when running on battery, <c>null</c> when
/// the codec couldn't determine the line state.
/// </param>
/// <param name="IsBatteryLow">
/// <c>true</c> when the device's "battery low" / imminent-shutdown signal
/// is active (Megatec Qx status bit 6); <c>false</c> when the device
/// explicitly reports the battery is above threshold; <c>null</c> when
/// the codec couldn't classify. Orthogonal to <see cref="BatteryStatus"/>
/// — a discharging UPS may be low or not, and a UPS on line power
/// recovering from depletion may still report low until the battery
/// climbs back above threshold.
/// </param>
public readonly record struct HidBatterySnapshot(
    int? BatteryChargePercent,
    BatteryStatus? BatteryStatus,
    bool? IsExternalPowerConnected,
    bool? IsBatteryLow);
