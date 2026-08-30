// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using static Periphery.Windows.DisplayConfigInterop;

namespace Periphery.Windows;

/// <summary>
/// Tier-3 display enrichment using the Windows DisplayConfig API (user32.dll).
/// Populates <see cref="DeviceInfo.MonitorName"/>,
/// <see cref="DeviceInfo.DisplayResolution"/>,
/// <see cref="DeviceInfo.DisplayBounds"/>,
/// <see cref="DeviceInfo.DisplayOrientation"/>,
/// <see cref="DeviceInfo.DisplayPhysicalConnector"/>, and
/// <see cref="DeviceInfo.DisplayConnectionKind"/> for Monitor devices.
///
/// <para>Does not require a Windows-specific TFM — all calls are plain Win32
/// P/Invoke via <see cref="DisplayConfigInterop"/>. See ADR-0018.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDisplayConfigEnricher
{
    private readonly IReadOnlyDictionary<string, DisplaySnapshot> _displays;

    private readonly record struct DisplaySnapshot(
        string?               FriendlyName,
        Size?                 Resolution,
        Rectangle?            Bounds,
        DisplayOrientation    Orientation,
        DisplayConnector      PhysicalConnector,
        DisplayConnectionKind ConnectionKind);

    private WindowsDisplayConfigEnricher(IReadOnlyDictionary<string, DisplaySnapshot> displays)
        => _displays = displays;

    /// <summary>
    /// Builds an enricher by calling <c>QueryDisplayConfig</c> once and
    /// resolving each active display path to its PnP instance ID via
    /// <c>CM_Get_Device_Interface_Property</c>.
    /// </summary>
    // ── Diagnostic tracing ────────────────────────────────────────────
    //
    // Set environment variable PERIPHERY_DISPLAYCONFIG_TRACE=1 to emit
    // step-by-step stderr output during enrichment. Intended for
    // diagnosing why MonitorName comes back null on a specific system
    // (e.g. Win10 IoT Enterprise, where a live box returned zero paths).
    // Default is silent so production code is unaffected.
    private static readonly bool TraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("PERIPHERY_DISPLAYCONFIG_TRACE"), "1", StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (TraceEnabled)
            Console.Error.WriteLine($"[DisplayConfigTrace] {message}");
    }

    internal static WindowsDisplayConfigEnricher Build()
    {
        var map = new Dictionary<string, DisplaySnapshot>(StringComparer.OrdinalIgnoreCase);
        Trace("Build() entered.");
        try
        {
            BuildCore(map);
        }
        catch (Exception ex)
        {
            Trace($"BuildCore THREW: {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"[DisplayConfigEnricher] Build failed: {ex.Message}");
        }
        Trace($"Build() exiting with {map.Count} snapshot(s) in map.");
        foreach (var kv in map)
            Trace($"  map['{kv.Key}'] = FriendlyName={kv.Value.FriendlyName ?? "(null)"} Connector={kv.Value.PhysicalConnector}");
        return new WindowsDisplayConfigEnricher(map);
    }

    private static unsafe void BuildCore(Dictionary<string, DisplaySnapshot> map)
    {
        int sizesRc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        Trace($"GetDisplayConfigBufferSizes rc={sizesRc} pathCount={pathCount} modeCount={modeCount}");
        if (sizesRc != 0)
            return;

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];

        int queryRc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        Trace($"QueryDisplayConfig rc={queryRc} effective pathCount={pathCount} modeCount={modeCount}");
        if (queryRc != 0)
            return;

        for (int i = 0; i < (int)pathCount; i++)
        {
            ref DisplayConfigPathInfo path = ref paths[i];
            Trace($"path[{i}]: adapterId={path.TargetInfo.AdapterId} id={path.TargetInfo.Id} outputTech={path.TargetInfo.OutputTechnology}");

            // ── Step 1: friendly name, connector type, device interface path ──

            var targetName = default(DisplayConfigTargetDeviceName);
            targetName.Header.Type      = DEVICE_INFO_GET_TARGET_NAME;
            targetName.Header.Size      = (uint)sizeof(DisplayConfigTargetDeviceName);
            targetName.Header.AdapterId = path.TargetInfo.AdapterId;
            targetName.Header.Id        = path.TargetInfo.Id;

            int targetRc = DisplayConfigGetDeviceInfo(&targetName);
            Trace($"  DisplayConfigGetDeviceInfo(GET_TARGET_NAME) rc={targetRc}");
            if (targetRc != 0)
                continue;

            string monitorDevicePath = new string(targetName.MonitorDevicePath).TrimEnd('\0');
            string rawFriendly = new string(targetName.MonitorFriendlyDeviceName).TrimEnd('\0');
            Trace($"  monitorDevicePath='{monitorDevicePath}'");
            Trace($"  MonitorFriendlyDeviceName='{rawFriendly}' (length={rawFriendly.Length})");
            Trace($"  outputTechnology={targetName.OutputTechnology}");

            if (string.IsNullOrEmpty(monitorDevicePath))
            {
                Trace("  -> SKIPPED: monitorDevicePath empty");
                continue;
            }

            // ── Step 2: resolve interface path → PnP instance ID ─────────────

            string? instanceId = DevNodeHelper.GetDeviceInterfaceInstanceId(monitorDevicePath);
            Trace($"  resolved instanceId='{instanceId ?? "(null)"}'");
            if (instanceId is null)
            {
                Trace("  -> SKIPPED: instanceId could not be resolved");
                continue;
            }

            // ── Step 3: native/preferred resolution ──────────────────────────

            var preferred = default(DisplayConfigTargetPreferredMode);
            preferred.Header.Type      = DEVICE_INFO_GET_TARGET_PREFERRED_MODE;
            preferred.Header.Size      = (uint)sizeof(DisplayConfigTargetPreferredMode);
            preferred.Header.AdapterId = path.TargetInfo.AdapterId;
            preferred.Header.Id        = path.TargetInfo.Id;

            Size? resolution = null;
            if (DisplayConfigGetDeviceInfo(&preferred) == 0 && preferred.Width > 0 && preferred.Height > 0)
                resolution = new Size((int)preferred.Width, (int)preferred.Height);

            // ── Step 4: current rotation and active bounds from source mode ───
            // sourceInfo.ModeInfoIdx indexes into the modes[] array when the path
            // is active; 0xffffffff means the index is not valid.
            //
            // The source mode's position is expressed in the ROTATED frame (it is
            // the origin Windows laid the panel out at) while its width/height
            // describe the UNROTATED source surface, so the two must be
            // reconciled before they can form one rectangle — see DisplayGeometry
            // and issue #163.

            var orientation = DisplayGeometry.FromCcdRotation(path.TargetInfo.Rotation);
            Trace($"  rotation={path.TargetInfo.Rotation} -> {orientation}");

            Rectangle? bounds = null;
            uint srcIdx = path.SourceInfo.ModeInfoIdx;
            if (srcIdx != 0xffffffff && srcIdx < modeCount
                && modes[srcIdx].InfoType == DisplayConfigModeInfo.TYPE_SOURCE
                && modes[srcIdx].SourceWidth > 0)
            {
                bounds = DisplayGeometry.DesktopBounds(
                    modes[srcIdx].SourcePositionX,
                    modes[srcIdx].SourcePositionY,
                    (int)modes[srcIdx].SourceWidth,
                    (int)modes[srcIdx].SourceHeight,
                    orientation);
            }

            // ── Assemble snapshot ─────────────────────────────────────────────

            string? friendlyName = new string(targetName.MonitorFriendlyDeviceName).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(friendlyName))
                friendlyName = null;

            map[instanceId] = new DisplaySnapshot(
                FriendlyName:      friendlyName,
                Resolution:        resolution,
                Bounds:            bounds,
                Orientation:       orientation,
                PhysicalConnector: MapConnector(targetName.OutputTechnology),
                ConnectionKind:    MapConnectionKind(targetName.OutputTechnology));
        }
    }

    internal DeviceInfo Enrich(DeviceInfo device)
    {
        if (device.Category != DeviceCategory.Monitor)
            return device;
        Trace($"Enrich() Monitor device.Id='{device.Id}'");

        bool hasSnap = _displays.TryGetValue(device.Id, out DisplaySnapshot snap);
        if (!hasSnap)
            Trace($"  -> NO MATCH in DisplayConfig map of {_displays.Count} entries. Map keys: [{string.Join(", ", _displays.Keys)}]");
        else
            Trace($"  -> MATCH: FriendlyName={snap.FriendlyName ?? "(null)"}");

        // Tier-3 fallback (ADR-0044): when DisplayConfig didn't give us a
        // FriendlyName — either because the device isn't in the map at all
        // or because DisplayConfig returned a snapshot with no friendly name
        // (e.g. Win10 IoT Enterprise builds where the DisplayConfig API
        // returns zero paths) — read the cached EDID from the registry and
        // parse the Display Product Name descriptor out of it. The other
        // enriched fields (resolution, bounds, connector) have no equivalent
        // registry source and stay null when DisplayConfig is unavailable.
        string? friendlyName = snap.FriendlyName;
        if (friendlyName is null)
        {
            friendlyName = WindowsEdidEnricher.GetMonitorFriendlyName(device.Id);
            if (friendlyName is not null)
                Trace($"  -> EDID-registry fallback yielded '{friendlyName}'");
            else
                Trace("  -> EDID-registry fallback yielded null");
        }

        if (!hasSnap)
        {
            // No DisplayConfig snapshot at all; only the friendly name
            // (if recovered from the EDID fallback) is available to set.
            return friendlyName is null
                ? device
                : device with { MonitorName = friendlyName };
        }

        return device with
        {
            MonitorName              = friendlyName ?? device.MonitorName,
            DisplayResolution        = snap.Resolution   ?? device.DisplayResolution,
            DisplayBounds            = snap.Bounds       ?? device.DisplayBounds,
            // The three below are non-nullable on the snapshot: reaching here means
            // a DisplayConfig path resolved, so each is always freshly measured and
            // overwrites unconditionally. Do not add `?? device.X` — it is a no-op
            // that reads as a fallback which does not exist.
            DisplayOrientation       = snap.Orientation,
            DisplayPhysicalConnector = snap.PhysicalConnector,
            DisplayConnectionKind    = snap.ConnectionKind,
        };
    }

    // ── Connector type mapping ────────────────────────────────────────────

    // `internal` for the same reason as MapConnectionKind: a pure total mapping
    // worth pinning without display hardware.
    internal static DisplayConnector MapConnector(int tech) => tech switch
    {
        OUTPUT_TECH_HD15            => DisplayConnector.Vga,
        OUTPUT_TECH_DVI             => DisplayConnector.Dvi,
        OUTPUT_TECH_HDMI            => DisplayConnector.Hdmi,
        OUTPUT_TECH_SDI             => DisplayConnector.Sdi,

        OUTPUT_TECH_DP_EXTERNAL or
        OUTPUT_TECH_DP_USB_TUNNEL   => DisplayConnector.DisplayPort,

        // The analogue-television family. DisplayConnector.AnalogTv exists for
        // exactly these and was previously unreachable — every one of them fell
        // through to Unknown, so the member could never be produced.
        OUTPUT_TECH_SVIDEO or
        OUTPUT_TECH_COMPOSITE_VIDEO or
        OUTPUT_TECH_COMPONENT_VIDEO or
        OUTPUT_TECH_D_JPN or
        OUTPUT_TECH_SDTVDONGLE      => DisplayConnector.AnalogTv,

        OUTPUT_TECH_LVDS or
        OUTPUT_TECH_DP_EMBEDDED or
        OUTPUT_TECH_UDI_EMBEDDED or
        OUTPUT_TECH_INTERNAL        => DisplayConnector.Internal,

        // UDI_EXTERNAL has no DisplayConnector member, so it stays Unknown
        // rather than being folded into a connector standard it is not.
        _                           => DisplayConnector.Unknown,
    };

    // The two indirect output technologies are NOT the same fact, and map to
    // SEPARATE kinds. INDIRECT_VIRTUAL is a software-presented display with no
    // physical panel. INDIRECT_WIRED is the general indirect-display path, which
    // DisplayLink adapters and USB-C / Thunderbolt docks also use to drive REAL
    // panels — Windows does not distinguish those from a synthetic IddCx rig
    // here, so calling it Virtual would misreport real glass. An IddCx display
    // (an IddSampleDriver rig) reports INDIRECT_WIRED, not
    // INDIRECT_VIRTUAL — mapping only the latter left those rigs misclassified as
    // Wired. This matches the control plane's MonitorOutputTechnology, which
    // likewise keeps IndirectWired and IndirectVirtual apart (ADR-0070 D2 /
    // ADR-0072).
    //
    // Every wired and internal technology is listed explicitly so the default arm
    // means "Windows reported something this build has never heard of" and can
    // answer Unknown. It used to answer Wired, which is what let INDIRECT_WIRED be
    // reported as a physical cable in the first place: an unrecognised value was
    // asserted to be cabled rather than admitted to be unknown. Enumerating the
    // whole SDK enum is what makes that default honest — flipping it alone would
    // have demoted genuinely-cabled outputs (SDI, S-Video, D_JPN…) to Unknown.
    //
    // `internal` so the pure mapping is unit-testable without display hardware.
    internal static DisplayConnectionKind MapConnectionKind(int tech) => tech switch
    {
        // "Embedded" outputs are internal connections; the SDK's Remarks direct
        // callers to process them in preference to the redundant INTERNAL value.
        OUTPUT_TECH_LVDS or
        OUTPUT_TECH_DP_EMBEDDED or
        OUTPUT_TECH_UDI_EMBEDDED or
        OUTPUT_TECH_INTERNAL          => DisplayConnectionKind.Internal,

        OUTPUT_TECH_HD15 or
        OUTPUT_TECH_SVIDEO or
        OUTPUT_TECH_COMPOSITE_VIDEO or
        OUTPUT_TECH_COMPONENT_VIDEO or
        OUTPUT_TECH_DVI or
        OUTPUT_TECH_HDMI or
        OUTPUT_TECH_D_JPN or
        OUTPUT_TECH_SDI or
        OUTPUT_TECH_DP_EXTERNAL or
        OUTPUT_TECH_UDI_EXTERNAL or
        OUTPUT_TECH_SDTVDONGLE or
        OUTPUT_TECH_DP_USB_TUNNEL     => DisplayConnectionKind.Wired,

        OUTPUT_TECH_MIRACAST          => DisplayConnectionKind.Wireless,

        // Kept APART (ADR-0072, superseding ADR-0071 D1). INDIRECT_WIRED is the
        // general indirect-display path: DisplayLink adapters and USB-C docks
        // drive REAL panels through it, alongside synthetic IddCx rigs, and
        // Windows does not distinguish them here. Virtual would assert "no
        // panel" about real glass; Wired would assert a cable about a synthetic
        // rig. Indirect asserts only what is actually known.
        OUTPUT_TECH_INDIRECT_WIRED    => DisplayConnectionKind.Indirect,
        OUTPUT_TECH_INDIRECT_VIRTUAL  => DisplayConnectionKind.Virtual,

        // OTHER (and the _FORCE_UINT32 sentinel, which shares its bit pattern)
        // plus anything a future Windows adds.
        _                             => DisplayConnectionKind.Unknown,
    };
}
