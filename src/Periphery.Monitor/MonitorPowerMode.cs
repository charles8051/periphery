// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// MCCS power-mode values (VCP 0xD6). Which values a given panel implements
/// is reported by its capabilities string; <see cref="On"/> and
/// <see cref="SoftOff"/> are the widely-supported pair.
/// </summary>
public enum MonitorPowerMode
{
    /// <summary>0x01 — panel on.</summary>
    On = 0x01,

    /// <summary>0x02 — DPMS standby.</summary>
    Standby = 0x02,

    /// <summary>0x03 — DPMS suspend.</summary>
    Suspend = 0x03,

    /// <summary>0x04 — panel off; controller still answering DDC.</summary>
    SoftOff = 0x04,

    /// <summary>0x05 — panel off as if by the power button (often write-only).</summary>
    HardOff = 0x05,
}
