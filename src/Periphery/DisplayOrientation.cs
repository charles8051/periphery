// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The monitor's current rotation relative to the panel's native orientation,
/// defined by <b>semantic value</b> (0°, 90°, 180°, 270°) rather than by any one
/// platform's rotation encoding.
/// </summary>
/// <remarks>
/// <para>
/// This is the property that makes a pure rotation observable. Without it a
/// rotation that leaves the monitor's origin on the virtual desktop unchanged —
/// the primary panel at (0,0) is the everyday case — produces an identical
/// <see cref="DeviceInfo.DisplayBounds"/> footprint under some layouts and no
/// other <see cref="DeviceInfo"/> field moves at all, so no
/// <c>DevicePropertyChanged</c> would be raised and a consumer would have no way
/// to learn the panel re-oriented (issue #163).
/// </para>
/// <para>
/// Populated on Windows from the CCD <c>DISPLAYCONFIG_PATH_TARGET_INFO.rotation</c>
/// of the monitor's active path. <c>null</c> means <b>unmeasured</b> — never
/// "unrotated": on Windows when DisplayConfig yields no path for the device, and
/// on Linux/macOS, whose providers do not read rotation yet. Both platforms can
/// supply it (DRM/KMS or RandR on Linux, <c>CGDisplayRotation</c> on macOS), so
/// this is an unimplemented backend, not a Windows-only concept — the same
/// incremental posture as every other field in this enrichment tier
/// (<see cref="DeviceInfo.DisplayResolution"/>, <see cref="DeviceInfo.DisplayBounds"/>,
/// <see cref="DeviceInfo.MonitorName"/>).
/// </para>
/// <para>
/// The numeric values are a stable, opaque serialization contract and are not
/// defined as any platform's native ordinal — each backend owns its own
/// translation (Windows DEVMODE <c>DMDO_*</c> is this ordinal, CCD
/// <c>DISPLAYCONFIG_ROTATION_*</c> is this ordinal + 1, X11 RandR is a bitmask,
/// Wayland folds rotation and flip into one transform).
/// </para>
/// <para>
/// This is the read-only <i>discovery</i>-plane spelling of the same concept the
/// <c>Periphery.Monitor</c> control plane exposes as
/// <c>Periphery.Monitor.MonitorOrientation</c> (ADR-0064). They are deliberately
/// separate types: <c>DeviceInfo</c> lives in core <c>Periphery</c>, which does
/// not — and must not — reference the optional monitor-control extension. The
/// two carry identical semantics and identical ordinals, so a consumer holding
/// both can map member-for-member. See ADR-0068.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayOrientation>))]
public enum DisplayOrientation
{
    /// <summary>Native orientation — 0° of rotation.</summary>
    Landscape = 0,

    /// <summary>Rotated 90°.</summary>
    Portrait = 1,

    /// <summary>Rotated 180°.</summary>
    LandscapeFlipped = 2,

    /// <summary>Rotated 270°.</summary>
    PortraitFlipped = 3,
}
