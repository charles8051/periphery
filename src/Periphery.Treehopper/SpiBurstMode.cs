// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// The SPI burst mode for a transfer. The enum value is the firmware's burst-mode
/// byte (verbatim from the original SDK).
/// </summary>
public enum SpiBurstMode : byte
{
    /// <summary>Full-duplex: clock in exactly as many MISO bytes as MOSI bytes sent.</summary>
    NoBurst = 0,

    /// <summary>
    /// Transmit-only: send MOSI bytes but return no MISO data. The fastest mode —
    /// it eliminates the read round-trip entirely.
    /// </summary>
    BurstTx = 1,

    /// <summary>
    /// Receive-only: clock in the requested number of MISO bytes without supplying
    /// MOSI data. The byte count is taken from the transfer buffer's length.
    /// </summary>
    BurstRx = 2,
}
