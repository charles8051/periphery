// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// The MCCS VCP codes the semantic surface touches. Any code — including
/// vendor-specific ones — can be driven through the raw
/// <see cref="MonitorDevice.GetVcpFeatureAsync"/> /
/// <see cref="MonitorDevice.SetVcpFeatureAsync"/> escape hatch; these
/// constants exist so the named helpers and consumers share one vocabulary.
/// </summary>
public static class VcpCode
{
    /// <summary>0x10 — Luminance (brightness), continuous.</summary>
    public const byte Luminance = 0x10;

    /// <summary>0x12 — Contrast, continuous.</summary>
    public const byte Contrast = 0x12;

    /// <summary>0x60 — Input source, non-continuous (see <see cref="MonitorInputSource"/>).</summary>
    public const byte InputSource = 0x60;

    /// <summary>0x62 — Audio speaker volume, continuous.</summary>
    public const byte AudioVolume = 0x62;

    /// <summary>0xD6 — Power mode, non-continuous (see <see cref="MonitorPowerMode"/>).</summary>
    public const byte PowerMode = 0xD6;
}
