// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace Periphery;

/// <summary>
/// Computes the set of property names that differ between two <see cref="DeviceInfo"/>
/// snapshots. Used by <see cref="DeviceWatcher"/> to populate
/// <see cref="DevicePropertyChangedEventArgs.ChangedProperties"/>.
/// </summary>
/// <remarks>
/// Covers all typed first-class properties on <see cref="DeviceInfo"/>.
/// The raw <c>Properties</c> bag is intentionally excluded — it contains
/// provider-internal OS properties and would generate noise on every
/// device modification event.
/// <para>
/// When adding a new property to <see cref="DeviceInfo"/>, add a corresponding
/// check here. The test <c>DeviceInfoDiffTests.AllTypedProperties_AreCoveredByDiff</c>
/// uses reflection to catch any missing entries.
/// </para>
/// </remarks>
internal static class DeviceInfoDiff
{
    /// <summary>
    /// Returns the set of property names that differ between
    /// <paramref name="previous"/> and <paramref name="current"/>.
    /// Returns an empty set when the snapshots are equivalent.
    /// </summary>
    internal static IReadOnlySet<string> Compute(DeviceInfo previous, DeviceInfo current)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);

        CheckRef(changed, nameof(DeviceInfo.Name),                     previous.Name,                     current.Name);
        CheckVal(changed, nameof(DeviceInfo.Category),                  previous.Category,                  current.Category);
        CheckRef(changed, nameof(DeviceInfo.Manufacturer),              previous.Manufacturer,              current.Manufacturer);
        CheckVal(changed, nameof(DeviceInfo.ClassGuid),                 previous.ClassGuid,                 current.ClassGuid);
        CheckRef(changed, nameof(DeviceInfo.ClassName),                  previous.ClassName,                  current.ClassName);
        CheckVal(changed, nameof(DeviceInfo.ContainerId),               previous.ContainerId,               current.ContainerId);
        CheckVal(changed, nameof(DeviceInfo.VendorId),                  previous.VendorId,                  current.VendorId);
        CheckVal(changed, nameof(DeviceInfo.ProductId),                 previous.ProductId,                 current.ProductId);
        CheckRef(changed, nameof(DeviceInfo.SerialNumber),              previous.SerialNumber,              current.SerialNumber);
        CheckVal(changed, nameof(DeviceInfo.IsActive),                  previous.IsActive,                  current.IsActive);
        CheckVal(changed, nameof(DeviceInfo.Status),                    previous.Status,                    current.Status);
        CheckVal(changed, nameof(DeviceInfo.BusType),                   previous.BusType,                   current.BusType);
        CheckRef(changed, nameof(DeviceInfo.LocationPath),              previous.LocationPath,              current.LocationPath);
        CheckRef(changed, nameof(DeviceInfo.Driver),                    previous.Driver,                    current.Driver);
        CheckRef(changed, nameof(DeviceInfo.DriverVersion),             previous.DriverVersion,             current.DriverVersion);
        CheckVal(changed, nameof(DeviceInfo.DisplayResolution),          previous.DisplayResolution,          current.DisplayResolution);
        CheckVal(changed, nameof(DeviceInfo.DisplayBounds),              previous.DisplayBounds,              current.DisplayBounds);
        CheckVal(changed, nameof(DeviceInfo.DisplayOrientation),         previous.DisplayOrientation,         current.DisplayOrientation);
        CheckRef(changed, nameof(DeviceInfo.MonitorName),                previous.MonitorName,                current.MonitorName);
        CheckVal(changed, nameof(DeviceInfo.DisplayPhysicalSizeInInches),previous.DisplayPhysicalSizeInInches,current.DisplayPhysicalSizeInInches);
        CheckVal(changed, nameof(DeviceInfo.DisplayDpi),                 previous.DisplayDpi,                 current.DisplayDpi);
        CheckVal(changed, nameof(DeviceInfo.DisplayPhysicalConnector),   previous.DisplayPhysicalConnector,   current.DisplayPhysicalConnector);
        CheckVal(changed, nameof(DeviceInfo.DisplayConnectionKind),      previous.DisplayConnectionKind,      current.DisplayConnectionKind);
        CheckVal(changed, nameof(DeviceInfo.DisplayUsageKind),           previous.DisplayUsageKind,           current.DisplayUsageKind);
        CheckVal(changed, nameof(DeviceInfo.DisplayMaxLuminanceInNits),  previous.DisplayMaxLuminanceInNits,  current.DisplayMaxLuminanceInNits);
        CheckVal(changed, nameof(DeviceInfo.DisplayMaxAvgLuminanceInNits),previous.DisplayMaxAvgLuminanceInNits,current.DisplayMaxAvgLuminanceInNits);
        CheckVal(changed, nameof(DeviceInfo.DisplayMinLuminanceInNits),  previous.DisplayMinLuminanceInNits,  current.DisplayMinLuminanceInNits);
        CheckVal(changed, nameof(DeviceInfo.DriveType),                 previous.DriveType,                 current.DriveType);
        CheckVal(changed, nameof(DeviceInfo.ParentId),                  previous.ParentId,                  current.ParentId);
        CheckVal(changed, nameof(DeviceInfo.PortNumber),                previous.PortNumber,                current.PortNumber);
        CheckVal(changed, nameof(DeviceInfo.UsbSpeed),                  previous.UsbSpeed,                  current.UsbSpeed);
        CheckVal(changed, nameof(DeviceInfo.MaxPowerMilliamps),         previous.MaxPowerMilliamps,         current.MaxPowerMilliamps);
        CheckVal(changed, nameof(DeviceInfo.UsbClassCode),              previous.UsbClassCode,              current.UsbClassCode);
        CheckVal(changed, nameof(DeviceInfo.HidUsagePage),              previous.HidUsagePage,              current.HidUsagePage);
        CheckVal(changed, nameof(DeviceInfo.HidUsage),                  previous.HidUsage,                  current.HidUsage);
        CheckVal(changed, nameof(DeviceInfo.HidMaxInputReportLength),   previous.HidMaxInputReportLength,   current.HidMaxInputReportLength);
        CheckVal(changed, nameof(DeviceInfo.HidMaxOutputReportLength),  previous.HidMaxOutputReportLength,  current.HidMaxOutputReportLength);
        CheckVal(changed, nameof(DeviceInfo.HidMaxFeatureReportLength), previous.HidMaxFeatureReportLength, current.HidMaxFeatureReportLength);
        CheckVal(changed, nameof(DeviceInfo.PortName),                  previous.PortName,                  current.PortName);
        CheckVal(changed, nameof(DeviceInfo.BatteryChargePercent),      previous.BatteryChargePercent,      current.BatteryChargePercent);
        CheckVal(changed, nameof(DeviceInfo.BatteryStatus),             previous.BatteryStatus,             current.BatteryStatus);
        CheckVal(changed, nameof(DeviceInfo.IsExternalPowerConnected),  previous.IsExternalPowerConnected,  current.IsExternalPowerConnected);
        CheckVal(changed, nameof(DeviceInfo.IsBatteryLow),              previous.IsBatteryLow,              current.IsBatteryLow);
        CheckVal(changed, nameof(DeviceInfo.Network),                   previous.Network,                   current.Network);
        CheckRef(changed, nameof(DeviceInfo.Subsystem),                  previous.Subsystem,                  current.Subsystem);
        CheckRef(changed, nameof(DeviceInfo.IOServiceClass),             previous.IOServiceClass,             current.IOServiceClass);

        // PhysicalAddress does not override Equals — compare by address bytes.
        if (!MacAddressEquals(previous.MacAddress, current.MacAddress))
            changed.Add(nameof(DeviceInfo.MacAddress));

        // ImmutableArray<T> struct equality is reference-based — compare element-wise.
        if (!IpAddressArrayEquals(previous.IPAddresses, current.IPAddresses))
            changed.Add(nameof(DeviceInfo.IPAddresses));

        // ImmutableHashSet<string> uses reference equality by default —
        // compare via SetEquals so two logically-equal tag sets don't
        // trigger a spurious change event (ADR-0047).
        if (!previous.Tags.SetEquals(current.Tags))
            changed.Add(nameof(DeviceInfo.Tags));

        return changed;
    }

    // Nullable value types — use Nullable.Equals which handles both nulls and IEquatable<T>.
    private static void CheckVal<T>(HashSet<string> changed, string name, T previous, T current)
        where T : struct
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
            changed.Add(name);
    }

    private static void CheckVal<T>(HashSet<string> changed, string name, T? previous, T? current)
        where T : struct
    {
        if (!Nullable.Equals(previous, current))
            changed.Add(name);
    }

    // Reference types — null-safe reference equality or Equals.
    private static void CheckRef<T>(HashSet<string> changed, string name, T? previous, T? current)
        where T : class
    {
        if (!Equals(previous, current))
            changed.Add(name);
    }

    private static bool MacAddressEquals(PhysicalAddress? a, PhysicalAddress? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.GetAddressBytes().SequenceEqual(b.GetAddressBytes());
    }

    private static bool IpAddressArrayEquals(ImmutableArray<IPAddress>? a, ImmutableArray<IPAddress>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        var aa = a.Value;
        var bb = b.Value;
        if (aa.Length != bb.Length) return false;
        for (int i = 0; i < aa.Length; i++)
            if (!aa[i].Equals(bb[i])) return false;
        return true;
    }
}
