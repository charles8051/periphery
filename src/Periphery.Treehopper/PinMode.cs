// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>The electrical mode of a Treehopper I/O pin.</summary>
public enum PinMode
{
    /// <summary>Unconfigured.</summary>
    Reserved = 0,

    /// <summary>High-impedance digital input.</summary>
    DigitalInput = 1,

    /// <summary>Push-pull digital output (drives both high and low).</summary>
    PushPullOutput = 2,

    /// <summary>Open-drain digital output (drives low; floats high).</summary>
    OpenDrainOutput = 3,

    /// <summary>12-bit analog input.</summary>
    AnalogInput = 4,
}
