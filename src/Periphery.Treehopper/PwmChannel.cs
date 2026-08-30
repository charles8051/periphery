// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// A hardware-PWM channel: <see cref="Pwm1"/>→pin 7, <see cref="Pwm2"/>→pin 8,
/// <see cref="Pwm3"/>→pin 9.
/// </summary>
/// <remarks>
/// Channels enable cumulatively (a Treehopper firmware constraint): driving
/// <see cref="Pwm2"/> also engages <see cref="Pwm1"/>, and <see cref="Pwm3"/>
/// engages both. The lease handles this transparently — an un-driven lower channel
/// simply outputs 0% duty.
/// </remarks>
public enum PwmChannel
{
    /// <summary>PWM channel 1 — pin 7.</summary>
    Pwm1 = 0,

    /// <summary>PWM channel 2 — pin 8.</summary>
    Pwm2 = 1,

    /// <summary>PWM channel 3 — pin 9.</summary>
    Pwm3 = 2,
}
