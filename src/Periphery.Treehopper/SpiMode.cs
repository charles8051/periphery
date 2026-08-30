// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// SPI clock polarity / phase. The integer values are the Treehopper MCU's
/// register encoding (bit 5 = CPHA, bit 4 = CPOL) — they go on the wire verbatim,
/// so do not cast plain 0/1/2/3 into this enum.
/// </summary>
public enum SpiMode : byte
{
    /// <summary>CPOL=0, CPHA=0 — clock idle-low, data valid on the rising edge.</summary>
    Mode00 = 0x00,

    /// <summary>CPOL=0, CPHA=1 — clock idle-low, data valid on the falling edge.</summary>
    Mode01 = 0x20,

    /// <summary>CPOL=1, CPHA=0 — clock idle-high, data valid on the rising edge.</summary>
    Mode10 = 0x10,

    /// <summary>CPOL=1, CPHA=1 — clock idle-high, data valid on the falling edge.</summary>
    Mode11 = 0x30,
}
