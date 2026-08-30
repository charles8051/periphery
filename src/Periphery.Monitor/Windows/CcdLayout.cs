// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Monitor.Windows;

/// <summary>
/// The CCD (QueryDisplayConfig / SetDisplayConfig) core behind
/// <see cref="MonitorLayout"/> and <see cref="MonitorLayoutApplier"/>
/// (ADR-0059). Read produces both the public layout snapshot and the raw
/// path/mode arrays the applier mutates, so one query feeds both surfaces.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CcdLayout
{
    internal sealed record RawTopology(
        MonitorLayout Layout,
        MonitorInterop.DisplayConfigPathInfo[] Paths,
        MonitorInterop.DisplayConfigModeInfo[] Modes,
        IReadOnlyDictionary<string, int> PathIndexByDeviceId);

    internal static unsafe RawTopology Read()
    {
        // Read once, up front: the session is a property of the process and does
        // not change under us, and every empty-return path below needs it to say
        // WHY it is empty (issue #207).
        uint sessionId = CurrentSessionId();

        var empty = new RawTopology(
            new MonitorLayout(
                ImmutableArray<MonitorLayoutEntry>.Empty,
                MonitorSessionVisibility.Classify(0, sessionId)),
            [], [], new Dictionary<string, int>());

        int rc = MonitorInterop.GetDisplayConfigBufferSizes(
            MonitorInterop.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (rc != 0 || pathCount == 0)
            return empty; // Headless / non-interactive session / LTSC zero paths (ADR-0044).

        var paths = new MonitorInterop.DisplayConfigPathInfo[pathCount];
        var modes = new MonitorInterop.DisplayConfigModeInfo[Math.Max(modeCount, 1)];
        fixed (MonitorInterop.DisplayConfigPathInfo* p = paths)
        fixed (MonitorInterop.DisplayConfigModeInfo* m = modes)
        {
            rc = MonitorInterop.QueryDisplayConfig(
                MonitorInterop.QDC_ONLY_ACTIVE_PATHS, ref pathCount, p, ref modeCount, m, IntPtr.Zero);
        }
        if (rc != 0 || pathCount == 0)
            return empty;

        var entries = new List<MonitorLayoutEntry>((int)pathCount);
        var primaryFacts = new List<MonitorPrimary.PathFacts>((int)pathCount);
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < pathCount; i++)
        {
            string? deviceId = GetTargetInstanceId(
                paths[i], out string? friendlyName, out uint outputTechnology,
                out MonitorPanelIdentity? panelId);
            if (deviceId is null)
                continue;

            uint srcIdx = paths[i].SourceInfo.ModeInfoIdx;
            if (srcIdx == MonitorInterop.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || srcIdx >= modeCount)
                continue;
            ref var source = ref modes[srcIdx];

            int refresh = RationalToHz(paths[i].TargetInfo.RefreshRate);
            // CurrentMode is the panel's NATIVE (unrotated) frame: the CCD
            // source mode reports pixels before rotation, so a portrait 1280x720
            // panel reads 1280x720 here (same frame as PreferredMode /
            // SupportedModes). The rotated virtual-desktop footprint is a
            // separate, derived fact — MonitorLayoutEntry.DesktopSize reconciles
            // this mode with Orientation below, so no consumer has to guess.
            var currentMode = new DisplayMode((int)source.SourceWidth, (int)source.SourceHeight, refresh);
            var position = new DisplayPosition(source.SourcePositionX, source.SourcePositionY);

            var orientation = CcdOrientation.FromCcdRotation(paths[i].TargetInfo.Rotation);
            var outputTech = CcdOutputTechnology.FromCcd(outputTechnology);

            string? sourceGdiName = GetSourceGdiName(paths[i]);
            var supported = sourceGdiName is null
                ? ImmutableArray<DisplayMode>.Empty
                : EnumerateSupportedModes(sourceGdiName);

            entries.Add(new MonitorLayoutEntry(
                deviceId,
                friendlyName,
                // Decided over the whole set below — not per-path from position,
                // which reports two primaries in clone mode (issue #138).
                IsPrimary: false,
                currentMode,
                GetPreferredMode(paths[i]),
                orientation,
                outputTech,
                position,
                supported)
            {
                PanelId = panelId,
            });
            primaryFacts.Add(new MonitorPrimary.PathFacts(sourceGdiName, position));
            indexById[deviceId] = i;
        }

        // Derive the single primary from the authoritative GDI signal, deduping
        // clone paths (issue #138). primaryIdx indexes the built entries.
        int primaryIdx = MonitorPrimary.SelectPrimaryIndex(
            primaryFacts, MonitorPathResolver.FindPrimarySourceGdiName());
        if (primaryIdx >= 0)
            entries[primaryIdx] = entries[primaryIdx] with { IsPrimary = true };

        return new RawTopology(
            new MonitorLayout(
                [.. entries],
                MonitorSessionVisibility.Classify(entries.Count, sessionId)),
            paths, modes, indexById);
    }

    /// <summary>
    /// The session this process runs in. Deliberately the BCL property and not
    /// WMI: <c>System.Management</c> is not trim/AOT safe and the <c>winmgmt</c>
    /// service is a runtime dependency that is disabled on exactly the hardened
    /// / IoT images this matters for (ADR-0009). No P/Invoke is needed either —
    /// and no polling, per ADR-0054.
    /// </summary>
    private static uint CurrentSessionId()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return (uint)process.SessionId;
        }
        catch
        {
            // Never let a diagnostic aid break the read it is annotating. An
            // unreadable session id degrades to "not session 0", so the layout
            // reports NoActiveDisplays — the same answer callers got before this
            // existed, rather than a fabricated blindness claim.
            return uint.MaxValue;
        }
    }

    private static unsafe string? GetTargetInstanceId(
        in MonitorInterop.DisplayConfigPathInfo path,
        out string? friendlyName, out uint outputTechnology,
        out MonitorPanelIdentity? panelId)
    {
        friendlyName = null;
        panelId = null;
        // OUTPUT_TECHNOLOGY_OTHER (0xFFFFFFFF) is the honest "unknown" sentinel to
        // report if the GET_TARGET_NAME query fails, so a caller never reads a
        // stale 0 (== HD15/VGA) for a query that never ran.
        outputTechnology = 0xFFFFFFFF;
        var target = new MonitorInterop.DisplayConfigTargetDeviceName
        {
            Header = new MonitorInterop.DisplayConfigDeviceInfoHeader
            {
                Type = MonitorInterop.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                Size = (uint)Marshal.SizeOf<MonitorInterop.DisplayConfigTargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };
        if (MonitorInterop.DisplayConfigGetDeviceInfo(&target) != 0)
            return null;

        // Read straight out of the query already issued for the instance id and
        // friendly name — Windows fills OutputTechnology in the same struct, so
        // surfacing MonitorOutputTechnology costs no extra interop call (ADR-0070).
        outputTechnology = target.OutputTechnology;
        // Same struct, same call: the EDID vendor/product were being read past and
        // dropped exactly like OutputTechnology was (ADR-0073).
        panelId = EdidIdentity.Decode(target.EdidManufactureId, target.EdidProductCodeId);

        string name = new((char*)target.MonitorFriendlyDeviceName);
        friendlyName = name.Length > 0 ? name : null;

        // Returns the instance id in whatever case the interface path carries --
        // lower-case in practice -- while core's DeviceInfo.Id, which comes from the
        // device-INSTANCE enumeration path, is upper-case. The documented join
        // between the two was 0-of-4 under an ordinal comparer (issue #190), which
        // is why MonitorLayoutEntry.DeviceId is typed as DeviceId (OrdinalIgnoreCase
        // by construction) rather than string.
        //
        // Do NOT try to "fix" this by resolving through
        // CM_Get_Device_Interface_Property(DEVPKEY_Device_InstanceId) instead: that
        // was measured on a 4-monitor box and returns the SAME lower-case string as
        // this transform, so it changes nothing. Windows genuinely reports one
        // instance id in different case from different APIs -- core's own snapshot
        // and change-notification paths disagree too (see WindowsDeviceMonitorProvider's
        // cache comment), which is why DeviceId exists.
        return MonitorPathResolver.DevicePathToInstanceId(new string((char*)target.MonitorDevicePath));
    }

    private static unsafe string? GetSourceGdiName(in MonitorInterop.DisplayConfigPathInfo path)
    {
        var source = new MonitorInterop.DisplayConfigSourceDeviceName
        {
            Header = new MonitorInterop.DisplayConfigDeviceInfoHeader
            {
                Type = MonitorInterop.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                Size = (uint)Marshal.SizeOf<MonitorInterop.DisplayConfigSourceDeviceName>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            },
        };
        return MonitorInterop.DisplayConfigGetDeviceInfo(&source) == 0
            ? new string((char*)source.ViewGdiDeviceName)
            : null;
    }

    private static unsafe DisplayMode? GetPreferredMode(in MonitorInterop.DisplayConfigPathInfo path)
    {
        var preferred = new MonitorInterop.DisplayConfigTargetPreferredMode
        {
            Header = new MonitorInterop.DisplayConfigDeviceInfoHeader
            {
                Type = MonitorInterop.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                Size = (uint)Marshal.SizeOf<MonitorInterop.DisplayConfigTargetPreferredMode>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };
        if (MonitorInterop.DisplayConfigGetDeviceInfo(&preferred) != 0
            || preferred.Width == 0 || preferred.Height == 0)
            return null;

        return new DisplayMode(
            (int)preferred.Width, (int)preferred.Height,
            RationalToHz(preferred.VSyncFreq));
    }

    internal static ImmutableArray<DisplayMode> EnumerateSupportedModes(string sourceGdiName)
    {
        var seen = new HashSet<(int, int, int)>();
        var result = ImmutableArray.CreateBuilder<DisplayMode>();
        var devMode = MonitorInterop.DevMode.Create();
        for (int i = 0; MonitorInterop.EnumDisplaySettingsEx(sourceGdiName, i, ref devMode, 0); i++)
        {
            var key = ((int)devMode.PelsWidth, (int)devMode.PelsHeight, (int)devMode.DisplayFrequency);
            if (seen.Add(key))
                result.Add(new DisplayMode(key.Item1, key.Item2, key.Item3));
        }
        return result.ToImmutable();
    }

    private static int RationalToHz(MonitorInterop.DisplayConfigRational rational) =>
        rational.Denominator == 0
            ? 0
            : (int)Math.Round(rational.Numerator / (double)rational.Denominator);
}
