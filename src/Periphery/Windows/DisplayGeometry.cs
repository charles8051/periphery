// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Drawing;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Pure display-geometry value logic for the Windows DisplayConfig (CCD)
/// enrichment tier: the CCD rotation encoding → <see cref="DisplayOrientation"/>
/// translation, and the reconciliation of a source mode's <b>unrotated</b>
/// surface size with its <b>rotated</b> virtual-desktop origin into one
/// internally-consistent rectangle.
/// </summary>
/// <remarks>
/// <para>
/// Total value transforms — no IO, no OS calls, no mutable state — so the
/// arithmetic that issue #163 got wrong is exhaustively unit testable without a
/// display attached. The imperative shell (<see cref="WindowsDisplayConfigEnricher"/>)
/// owns the <c>QueryDisplayConfig</c> call; this class owns only the math.
/// </para>
/// <para>
/// The bug this exists to prevent: <c>DISPLAYCONFIG_SOURCE_MODE</c> mixes two
/// frames of reference. <c>position</c> is the monitor's origin on the virtual
/// desktop, which Windows lays out using the <i>rotated</i> footprint, while
/// <c>width</c>/<c>height</c> describe the source surface, which is <i>not</i>
/// rotated. Combining them verbatim yields a rectangle whose origin and size
/// disagree — for a 1920×1080 panel rotated to portrait at x=-1080, a
/// 1920-wide rect at x=-1080 extends to +840 and overlaps its neighbour, so it
/// cannot describe any real desktop layout.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DisplayGeometry
{
    /// <summary>
    /// CCD <c>DISPLAYCONFIG_ROTATION_IDENTITY</c>. The four CCD rotation values
    /// (IDENTITY / ROTATE90 / ROTATE180 / ROTATE270) are 1..4 — the DEVMODE
    /// <c>DMDO_*</c> ordinal plus one.
    /// </summary>
    internal const int DISPLAYCONFIG_ROTATION_IDENTITY = 1;

    /// <summary>
    /// Maps a CCD <c>DISPLAYCONFIG_ROTATION_*</c> value to the contract value.
    /// Total: anything outside 1..4 (including the <c>_FORCE_UINT32</c> sentinel
    /// and the 0 a zero-initialised struct carries) reads as
    /// <see cref="DisplayOrientation.Landscape"/>, matching how Windows treats an
    /// unrotated path.
    /// </summary>
    internal static DisplayOrientation FromCcdRotation(int rotation) => rotation switch
    {
        DISPLAYCONFIG_ROTATION_IDENTITY     => DisplayOrientation.Landscape,
        DISPLAYCONFIG_ROTATION_IDENTITY + 1 => DisplayOrientation.Portrait,
        DISPLAYCONFIG_ROTATION_IDENTITY + 2 => DisplayOrientation.LandscapeFlipped,
        DISPLAYCONFIG_ROTATION_IDENTITY + 3 => DisplayOrientation.PortraitFlipped,
        _                                   => DisplayOrientation.Landscape,
    };

    /// <summary>True when the orientation is portrait-class (90° or 270°) and the
    /// source surface's width and height are therefore transposed on the desktop.</summary>
    internal static bool IsPortrait(DisplayOrientation orientation) =>
        orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;

    /// <summary>
    /// The monitor's rectangle on the virtual desktop: the CCD source position
    /// (already expressed in the rotated frame) combined with the source surface
    /// size <b>transposed for a portrait-class rotation</b>, so origin and size
    /// share one frame of reference and the rectangle describes the panel's real
    /// on-desktop footprint — the same rectangle the OS window system reports.
    /// </summary>
    internal static Rectangle DesktopBounds(
        int positionX,
        int positionY,
        int sourceWidth,
        int sourceHeight,
        DisplayOrientation orientation) =>
        IsPortrait(orientation)
            ? new Rectangle(positionX, positionY, sourceHeight, sourceWidth)
            : new Rectangle(positionX, positionY, sourceWidth, sourceHeight);
}
