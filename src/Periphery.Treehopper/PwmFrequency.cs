// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// Hardware-PWM base frequency. This is a global setting shared by all three PWM
/// channels (the firmware derives each from the same timer).
/// </summary>
public enum PwmFrequency : byte
{
    /// <summary>732 Hz (default).</summary>
    Freq732Hz = 0,

    /// <summary>183 Hz.</summary>
    Freq183Hz = 1,

    /// <summary>61 Hz.</summary>
    Freq61Hz = 2,
}
