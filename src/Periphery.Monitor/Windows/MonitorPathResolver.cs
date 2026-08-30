// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Monitor.Windows;

/// <summary>
/// Resolves a Periphery monitor identity (the SetupAPI device-instance ID,
/// e.g. <c>DISPLAY\GSM5BBF\5&amp;2e2fefea&amp;3&amp;UID33024</c>) into the two
/// Windows handles the backends need: the owning source's GDI device name
/// (<c>\\.\DISPLAY1</c>, for the mode-set surface) and the <c>HMONITOR</c>
/// whose physical monitors carry the DDC channel.
/// </summary>
/// <remarks>
/// Same correlation the core's <c>WindowsDisplayConfigEnricher</c> performs,
/// self-contained per the family precedent (ADR-0058 OQ-001): walk
/// <c>QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)</c>, match each target's
/// <c>monitorDevicePath</c> against the instance ID, then read the path's
/// source GDI name and find the <c>HMONITOR</c> bearing it.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class MonitorPathResolver
{
    internal readonly record struct ResolvedMonitor(string SourceGdiName, IntPtr HMonitor);

    internal static ResolvedMonitor Resolve(string deviceId)
    {
        string? sourceName = FindSourceGdiName(deviceId);
        if (sourceName is null)
            throw new MonitorDeviceNotFoundException(
                $"Monitor '{deviceId}' is not on this session's active display paths. "
                + "Display configuration is session-local: a remote (RDP/SSH/service) session "
                + "sees only its own virtual display, not the console session's physical "
                + "monitors (ADR-0058 OQ-004). Run from the interactive console session — "
                + "or the monitor was unplugged/disabled between enumeration and open.",
                deviceId);

        IntPtr hMonitor = FindHMonitor(sourceName);
        if (hMonitor == IntPtr.Zero)
            throw new MonitorDeviceNotFoundException(
                $"Monitor '{deviceId}' resolved to source '{sourceName}', but no HMONITOR "
                + "carries that device name. The display may have just been reconfigured.",
                deviceId);

        return new ResolvedMonitor(sourceName, hMonitor);
    }

    private static unsafe string? FindSourceGdiName(string deviceId)
    {
        int rc = MonitorInterop.GetDisplayConfigBufferSizes(
            MonitorInterop.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (rc != 0 || pathCount == 0)
            return null;

        var paths = new MonitorInterop.DisplayConfigPathInfo[pathCount];
        var modes = new MonitorInterop.DisplayConfigModeInfo[Math.Max(modeCount, 1)];
        fixed (MonitorInterop.DisplayConfigPathInfo* p = paths)
        fixed (MonitorInterop.DisplayConfigModeInfo* m = modes)
        {
            rc = MonitorInterop.QueryDisplayConfig(
                MonitorInterop.QDC_ONLY_ACTIVE_PATHS, ref pathCount, p, ref modeCount, m, IntPtr.Zero);
        }
        if (rc != 0)
            return null;

        for (int i = 0; i < pathCount; i++)
        {
            var target = new MonitorInterop.DisplayConfigTargetDeviceName
            {
                Header = new MonitorInterop.DisplayConfigDeviceInfoHeader
                {
                    Type = MonitorInterop.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    Size = (uint)Marshal.SizeOf<MonitorInterop.DisplayConfigTargetDeviceName>(),
                    AdapterId = paths[i].TargetInfo.AdapterId,
                    Id = paths[i].TargetInfo.Id,
                },
            };
            if (MonitorInterop.DisplayConfigGetDeviceInfo(&target) != 0)
                continue;

            string devicePath = new((char*)target.MonitorDevicePath);
            if (!InstanceIdMatchesDevicePath(deviceId, devicePath))
                continue;

            var source = new MonitorInterop.DisplayConfigSourceDeviceName
            {
                Header = new MonitorInterop.DisplayConfigDeviceInfoHeader
                {
                    Type = MonitorInterop.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    Size = (uint)Marshal.SizeOf<MonitorInterop.DisplayConfigSourceDeviceName>(),
                    AdapterId = paths[i].SourceInfo.AdapterId,
                    Id = paths[i].SourceInfo.Id,
                },
            };
            if (MonitorInterop.DisplayConfigGetDeviceInfo(&source) != 0)
                continue;

            return new string((char*)source.ViewGdiDeviceName);
        }

        return null;
    }

    /// <summary>
    /// Matches a SetupAPI instance ID against a device-interface path:
    /// <c>\\?\DISPLAY#GSM5BBF#5&amp;…&amp;UID33024#{guid}</c> carries the
    /// instance in its first three <c>#</c>-separated segments with
    /// <c>\</c> replaced by <c>#</c>.
    /// </summary>
    internal static bool InstanceIdMatchesDevicePath(string instanceId, string devicePath) =>
        DevicePathToInstanceId(devicePath) is { } normalized
        && string.Equals(normalized, instanceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a monitor device-interface path into its SetupAPI
    /// instance-ID form (strip <c>\\?\</c> and the interface GUID, then
    /// <c>#</c> → <c>\</c>).
    /// </summary>
    internal static string? DevicePathToInstanceId(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
            return null;

        ReadOnlySpan<char> path = devicePath.AsSpan();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        // Drop the trailing "#{interface-guid}".
        int guidStart = path.LastIndexOf('#');
        if (guidStart > 0 && guidStart + 1 < path.Length && path[guidStart + 1] == '{')
            path = path[..guidStart];

        return path.ToString().Replace('#', '\\');
    }

    /// <summary>
    /// The GDI source name (<c>\\.\DISPLAY1</c>) of the monitor Windows flags
    /// as primary (<c>MONITORINFOF_PRIMARY</c>), or <see langword="null"/> when
    /// enumeration finds none. This is the authoritative single-primary signal
    /// behind <see cref="MonitorPrimary.SelectPrimaryIndex"/> (issue #138) —
    /// unlike the CCD source position it is set on exactly one monitor even in
    /// clone / duplicate mode.
    /// </summary>
    internal static unsafe string? FindPrimarySourceGdiName()
    {
        var found = new List<IntPtr>();
        var gcHandle = GCHandle.Alloc(found);
        try
        {
            _ = MonitorInterop.EnumDisplayMonitors(
                IntPtr.Zero, IntPtr.Zero, &CollectMonitor, GCHandle.ToIntPtr(gcHandle));
        }
        finally
        {
            gcHandle.Free();
        }

        foreach (IntPtr hMonitor in found)
        {
            var info = new MonitorInterop.MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<MonitorInterop.MonitorInfoEx>(),
            };
            if (!MonitorInterop.GetMonitorInfo(hMonitor, &info))
                continue;

            if ((info.Flags & MonitorInterop.MONITORINFOF_PRIMARY) != 0)
                return new string((char*)info.Device);
        }

        return null;
    }

    private static unsafe IntPtr FindHMonitor(string sourceGdiName)
    {
        var found = new List<IntPtr>();
        var gcHandle = GCHandle.Alloc(found);
        try
        {
            _ = MonitorInterop.EnumDisplayMonitors(
                IntPtr.Zero, IntPtr.Zero, &CollectMonitor, GCHandle.ToIntPtr(gcHandle));
        }
        finally
        {
            gcHandle.Free();
        }

        foreach (IntPtr hMonitor in found)
        {
            var info = new MonitorInterop.MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<MonitorInterop.MonitorInfoEx>(),
            };
            if (!MonitorInterop.GetMonitorInfo(hMonitor, &info))
                continue;

            string device = new((char*)info.Device);
            if (string.Equals(device, sourceGdiName, StringComparison.OrdinalIgnoreCase))
                return hMonitor;
        }

        return IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data)
    {
        var list = (List<IntPtr>)GCHandle.FromIntPtr(data).Target!;
        list.Add(hMonitor);
        return 1; // Continue enumeration.
    }
}
