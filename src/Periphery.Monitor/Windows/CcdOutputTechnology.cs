// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;

namespace Periphery.Monitor.Windows;

/// <summary>
/// The single, explicit, total translation from the Windows CCD
/// <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> value (as carried by
/// <c>DisplayConfigTargetDeviceName.OutputTechnology</c>) to the platform-neutral
/// <see cref="MonitorOutputTechnology"/> contract value.
/// </summary>
/// <remarks>
/// This helper is what lets <see cref="MonitorOutputTechnology"/> be a semantic
/// contract type instead of a re-labelled Windows <c>uint</c> (ADR-0064): no
/// code outside this file interprets a raw output-technology value, so the
/// enum's ordinals are not load-bearing at the platform boundary. A non-Windows
/// backend supplies its own analogous mapping (a Linux DRM connector type, an
/// X11/RandR output) without touching the contract enum.
/// <para>
/// The mapping is deliberately read-only — Windows exposes no way to <i>set</i>
/// a monitor's output technology, so there is no <c>ToCcd</c> counterpart, in
/// contrast to <see cref="CcdOrientation"/>. It is total: any value the contract
/// does not model (S-Video, composite/component, LVDS, SDI, Miracast, the raw
/// <c>OTHER</c>/<c>_FORCE_UINT32</c> sentinels) maps to
/// <see cref="MonitorOutputTechnology.Other"/>.
/// </para>
/// <para>
/// <c>INDIRECT_WIRED</c> and <c>INDIRECT_VIRTUAL</c> map to <b>separate</b>
/// contract members. They are not interchangeable: <c>INDIRECT_WIRED</c> is
/// reported both by synthetic IddCx rigs and by DisplayLink / USB-C dock
/// adapters driving real panels, so collapsing the pair into one "virtual"
/// value would misreport real glass as virtual (ADR-0070 D2).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class CcdOutputTechnology
{
    /// <summary>
    /// Maps a CCD <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> value to the
    /// platform-neutral contract value. Total — unmapped values read as
    /// <see cref="MonitorOutputTechnology.Other"/>.
    /// </summary>
    internal static MonitorOutputTechnology FromCcd(uint outputTechnology) => outputTechnology switch
    {
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => MonitorOutputTechnology.Internal,
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HD15 => MonitorOutputTechnology.Vga,
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI => MonitorOutputTechnology.Dvi,
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => MonitorOutputTechnology.Hdmi,
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => MonitorOutputTechnology.DisplayPortExternal,
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => MonitorOutputTechnology.DisplayPortEmbedded,
        // Kept DISTINCT, deliberately: INDIRECT_WIRED covers DisplayLink adapters
        // and USB-C docks driving REAL panels as well as synthetic IddCx rigs, so
        // folding it into a single "virtual" value would report a false virtual
        // for real glass. The consumer collapses these if it wants to; Periphery
        // does not guess (ADR-0070 D2).
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED => MonitorOutputTechnology.IndirectWired,
        MonitorInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL => MonitorOutputTechnology.IndirectVirtual,
        _ => MonitorOutputTechnology.Other,
    };
}
