// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// Display rotation relative to the panel's native orientation, defined by
/// <b>semantic value</b> (0°, 90°, 180°, 270° of clockwise rotation) — not by
/// any one platform's native rotation encoding. This is a platform-neutral
/// contract type: consumers reason about it by name, and each backend owns the
/// translation to and from its OS representation (ADR-0064).
/// </summary>
/// <remarks>
/// <para>
/// The numeric values are a stable, opaque serialization contract; they are
/// <i>not</i> defined as, and must not be assumed equal to, any platform's
/// native ordinal. On Windows the CCD read/apply and DEVMODE mode-set paths map
/// through <c>Periphery.Monitor.Windows.CcdOrientation</c>, so the enum's
/// ordinals are not load-bearing at the OS boundary; a future backend maps
/// differently:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Windows</b> — DEVMODE <c>DMDO_DEFAULT/90/180/270</c> (0–3) and CCD
///     <c>DISPLAYCONFIG_ROTATION_*</c> (that ordinal + 1).
///   </description></item>
///   <item><description>
///     <b>X11 (RandR)</b> — a rotation <i>bitmask</i>
///     (<c>RR_Rotate_0/90/180/270</c>), independent of and combinable with
///     reflection flags; not an ordinal.
///   </description></item>
///   <item><description>
///     <b>Wayland</b> — a <c>wl_output</c> transform enum, which folds rotation
///     and flip into one value.
///   </description></item>
/// </list>
/// <para>
/// Reflected/flipped-only geometries that some backends can express (e.g. X11
/// <c>RR_Reflect_*</c>, the Wayland <c>*_FLIPPED_*</c> transforms) have no
/// member here today; the four rotation states are the modeled contract. A
/// backend that needs to surface reflection is a contract extension, not a
/// silent reinterpretation of these values.
/// </para>
/// </remarks>
public enum MonitorOrientation
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
