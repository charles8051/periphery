// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// The voltage reference an analog-input pin is measured against. The enum value is
/// the firmware's reference-level byte (verbatim from the original SDK); it is sent
/// in the pin-config packet when the pin is made an analog input.
/// </summary>
public enum AdcReferenceLevel : byte
{
    /// <summary>3.3 V rail from the on-board LDO, ±1.5% (the default).</summary>
    Vref_3V3 = 0,

    /// <summary>1.65 V on-chip reference, ±1.8%.</summary>
    Vref_1V65 = 1,

    /// <summary>1.85 V on-chip reference (effective 1.8 V).</summary>
    Vref_1V85 = 2,

    /// <summary>2.4 V on-chip reference, ±2.1%.</summary>
    Vref_2V4 = 3,

    /// <summary>3.3 V (effective) derived from the 1.65 V reference, ±3.6%.</summary>
    Vref_3V3Derived = 4,

    /// <summary>3.7 V (effective 3.6 V) derived from the 1.85 V LDO.</summary>
    Vref_3V7 = 5,
}

/// <summary>Extensions for <see cref="AdcReferenceLevel"/>.</summary>
public static class AdcReferenceLevelExtensions
{
    /// <summary>
    /// The effective reference voltage in volts, used to convert a raw ADC sample to
    /// a voltage. Mirrors the original SDK's mapping (note 1V85 → 1.8 V and
    /// 3V7 → 3.6 V effective).
    /// </summary>
    public static double ReferenceVoltage(this AdcReferenceLevel level) => level switch
    {
        AdcReferenceLevel.Vref_1V65        => 1.65,
        AdcReferenceLevel.Vref_1V85        => 1.8,
        AdcReferenceLevel.Vref_2V4         => 2.4,
        AdcReferenceLevel.Vref_3V3         => 3.3,
        AdcReferenceLevel.Vref_3V3Derived  => 3.3,
        AdcReferenceLevel.Vref_3V7         => 3.6,
        _                                  => 3.3,
    };
}
