// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// An immutable snapshot of the Treehopper's pin state at a given moment.
/// Produced by <see cref="TreehopperWire.DecodeReport"/> from a raw 41-byte
/// pin-state buffer off the IN endpoint. (ADR-0052 DEC-002.)
/// </summary>
/// <param name="Sequence">Monotonically increasing counter, set by the producer.</param>
/// <param name="Pins">One <see cref="PinSnapshot"/> per pin (index 0–19).</param>
public sealed record BoardReport(long Sequence, ImmutableArray<PinSnapshot> Pins);

/// <summary>
/// The last-known state of a single Treehopper pin as decoded from a pin-state
/// report. Values are as reported by the board at the time of the report, not
/// as configured by the host.
/// </summary>
/// <param name="Digital">
/// <c>true</c> if the pin is logic-high. Meaningful for digital-input and
/// push-pull / open-drain output pins.
/// </param>
/// <param name="Adc">
/// Raw 12-bit ADC sample (0–4092). Meaningful only for analog-input pins;
/// 0 for other modes.
/// </param>
public readonly record struct PinSnapshot(bool Digital, int Adc)
{
    /// <summary>ADC sample scaled to 0.0–1.0 (using the 4092-count full-scale).</summary>
    public double AnalogValue => Adc / TreehopperWire.AdcDivisor;

    /// <summary>
    /// ADC sample as a voltage against a chosen analog reference. The snapshot is
    /// dimensionless, so the reference is the caller's to supply; it defaults to the
    /// board's 3.3 V supply rail.
    /// </summary>
    /// <param name="referenceVoltage">The full-scale reference voltage. Default 3.3 V.</param>
    public double AnalogVoltage(double referenceVoltage = 3.3) => AnalogValue * referenceVoltage;
}
