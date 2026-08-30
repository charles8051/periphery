// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// Common MCCS input-source values (VCP 0x60). The set a panel actually
/// implements is listed in its capabilities string; vendor-specific values
/// outside this enum go through the raw VCP surface.
/// </summary>
public enum MonitorInputSource
{
    /// <summary>0x01 — VGA / analog 1.</summary>
    Vga1 = 0x01,

    /// <summary>0x03 — DVI 1.</summary>
    Dvi1 = 0x03,

    /// <summary>0x04 — DVI 2.</summary>
    Dvi2 = 0x04,

    /// <summary>0x0F — DisplayPort 1.</summary>
    DisplayPort1 = 0x0F,

    /// <summary>0x10 — DisplayPort 2.</summary>
    DisplayPort2 = 0x10,

    /// <summary>0x11 — HDMI 1.</summary>
    Hdmi1 = 0x11,

    /// <summary>0x12 — HDMI 2.</summary>
    Hdmi2 = 0x12,
}
