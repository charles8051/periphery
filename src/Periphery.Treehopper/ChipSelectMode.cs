// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// How a SPI chip-select pin is driven across a transaction. The enum value is the
/// firmware's chip-select-mode byte (verbatim from the original SDK).
/// </summary>
public enum ChipSelectMode : byte
{
    /// <summary>Asserted low for the transaction, returned high afterwards (the common default).</summary>
    SpiActiveLow = 0,

    /// <summary>Asserted high for the transaction, returned low afterwards.</summary>
    SpiActiveHigh = 1,

    /// <summary>Pulsed high once before the transaction begins.</summary>
    PulseHighAtBeginning = 2,

    /// <summary>Pulsed high once after the transaction completes.</summary>
    PulseHighAtEnd = 3,

    /// <summary>Pulsed low once before the transaction begins.</summary>
    PulseLowAtBeginning = 4,

    /// <summary>Pulsed low once after the transaction completes.</summary>
    PulseLowAtEnd = 5,
}
