// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Populates battery power fields for <see cref="DeviceCategory.Battery"/> devices
/// from system-wide Win32 power telemetry.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsBatteryEnricher
{
    private const byte AcOffline = 0;
    private const byte AcOnline = 1;
    private const byte AcUnknown = 255;

    private const byte BatteryFlagCritical = 0x04;
    private const byte BatteryFlagCharging = 0x08;
    private const byte BatteryFlagNoBattery = 0x80;
    private const byte BatteryFlagUnknown = 255;
    private const byte BatteryPercentUnknown = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    internal readonly record struct BatterySnapshot(
        int? BatteryChargePercent,
        BatteryStatus? BatteryStatus,
        bool? IsExternalPowerConnected,
        bool? IsBatteryLow);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS systemPowerStatus);

    internal static BatterySnapshot? TryReadSnapshot()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps))
            return null;

        bool hasBattery = sps.BatteryFlag != BatteryFlagNoBattery;

        int? percent = sps.BatteryLifePercent == BatteryPercentUnknown || !hasBattery
            ? null
            : sps.BatteryLifePercent;

        bool? isExternalPowerConnected = sps.ACLineStatus switch
        {
            AcOnline => true,
            AcOffline => false,
            AcUnknown => null,
            _ => null,
        };

        BatteryStatus? batteryStatus = hasBattery
            ? MapBatteryStatus(sps.BatteryFlag, percent, isExternalPowerConnected)
            : null;

        bool? isBatteryLow = (hasBattery, sps.BatteryFlag) switch
        {
            (false, _) => null,                              // No battery present.
            (true, BatteryFlagUnknown) => null,              // OS couldn't read flags.
            (true, var flag) => (flag & BatteryFlagCritical) != 0,
        };

        return new BatterySnapshot(percent, batteryStatus, isExternalPowerConnected, isBatteryLow);
    }

    internal static DeviceInfo Enrich(DeviceInfo device, BatterySnapshot? snapshot)
    {
        if (device.Category != DeviceCategory.Battery || snapshot is null)
            return device;

        return device with
        {
            BatteryChargePercent = snapshot.Value.BatteryChargePercent,
            BatteryStatus = snapshot.Value.BatteryStatus,
            IsExternalPowerConnected = snapshot.Value.IsExternalPowerConnected,
            IsBatteryLow = snapshot.Value.IsBatteryLow,
            // Per ADR-0047, also annotate with the cross-cutting Battery
            // tag so consumers can filter via WithTag(DeviceTags.Battery)
            // and pick up batteries surfaced under any Category (this
            // enricher only handles Category=Battery; a HID-class UPS will
            // be tagged by Periphery.Hid's HidBatteryEnricher per ADR-0048).
            Tags = device.Tags.Add(DeviceTags.Battery),
        };
    }

    internal static BatteryStatus MapBatteryStatus(byte batteryFlag, int? batteryPercent, bool? isExternalPowerConnected)
    {
        if (batteryFlag == BatteryFlagUnknown)
            return BatteryStatus.Unknown;

        if ((batteryFlag & BatteryFlagCharging) != 0)
            return BatteryStatus.Charging;

        if (isExternalPowerConnected == true)
            return batteryPercent is >= 100 ? BatteryStatus.Full : BatteryStatus.NotCharging;

        if (isExternalPowerConnected == false)
            return BatteryStatus.Discharging;

        return BatteryStatus.Unknown;
    }
}