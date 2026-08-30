// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;

namespace Periphery.Monitor.Windows;

/// <summary>
/// The single, explicit, total translation between the platform-neutral
/// <see cref="MonitorOrientation"/> contract value and the two Windows
/// encodings of display rotation: the DEVMODE <c>dmDisplayOrientation</c>
/// ordinal (<c>DMDO_DEFAULT</c>/<c>DMDO_90</c>/<c>DMDO_180</c>/<c>DMDO_270</c>
/// = 0/1/2/3) and the CCD <c>DISPLAYCONFIG_ROTATION_*</c> value (DEVMODE
/// ordinal + 1).
/// </summary>
/// <remarks>
/// This helper is what lets <see cref="MonitorOrientation"/> be a semantic
/// contract type instead of a re-labelled Windows ordinal (ADR-0064): no code
/// outside this file casts between the enum's numeric value and an OS rotation
/// value, so the enum's ordinals are not load-bearing at any platform boundary.
/// A non-Windows backend supplies its own analogous mapping — an X11 RandR
/// rotation <i>bitmask</i> (<c>RR_Rotate_0/90/180/270</c>) or a Wayland
/// <c>wl_output</c> transform — without touching the contract enum.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class CcdOrientation
{
    /// <summary>Maps a DEVMODE <c>dmDisplayOrientation</c> ordinal to the contract value.</summary>
    internal static MonitorOrientation FromDevMode(uint dmDisplayOrientation) => dmDisplayOrientation switch
    {
        0 => MonitorOrientation.Landscape,          // DMDO_DEFAULT
        1 => MonitorOrientation.Portrait,           // DMDO_90
        2 => MonitorOrientation.LandscapeFlipped,   // DMDO_180
        3 => MonitorOrientation.PortraitFlipped,    // DMDO_270
        _ => MonitorOrientation.Landscape,
    };

    /// <summary>Maps the contract value to a DEVMODE <c>dmDisplayOrientation</c> ordinal.</summary>
    internal static uint ToDevMode(MonitorOrientation orientation) => orientation switch
    {
        MonitorOrientation.Landscape => 0,          // DMDO_DEFAULT
        MonitorOrientation.Portrait => 1,           // DMDO_90
        MonitorOrientation.LandscapeFlipped => 2,   // DMDO_180
        MonitorOrientation.PortraitFlipped => 3,    // DMDO_270
        _ => 0,
    };

    /// <summary>Maps a CCD <c>DISPLAYCONFIG_ROTATION_*</c> value to the contract value.</summary>
    internal static MonitorOrientation FromCcdRotation(uint rotation) =>
        rotation is >= MonitorInterop.DISPLAYCONFIG_ROTATION_IDENTITY and <= MonitorInterop.DISPLAYCONFIG_ROTATION_IDENTITY + 3
            ? FromDevMode(rotation - MonitorInterop.DISPLAYCONFIG_ROTATION_IDENTITY)
            : MonitorOrientation.Landscape;

    /// <summary>Maps the contract value to a CCD <c>DISPLAYCONFIG_ROTATION_*</c> value.</summary>
    internal static uint ToCcdRotation(MonitorOrientation orientation) =>
        ToDevMode(orientation) + MonitorInterop.DISPLAYCONFIG_ROTATION_IDENTITY;
}
