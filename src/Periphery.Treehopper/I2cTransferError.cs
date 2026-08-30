// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper;

/// <summary>
/// The status byte returned by an I2C transaction (the leading byte of the
/// peripheral response). <see cref="Success"/> is the sentinel <c>0xFF</c>.
/// </summary>
public enum I2cTransferError : byte
{
    /// <summary>Bus arbitration was lost to another master.</summary>
    ArbitrationLost = 0,

    /// <summary>The addressed device did not acknowledge (NACK) — usually "no device at this address".</summary>
    Nack = 1,

    /// <summary>An unspecified bus error occurred.</summary>
    Unknown = 2,

    /// <summary>The transmit buffer underran mid-transaction.</summary>
    TxUnderrun = 3,

    /// <summary>The transaction completed successfully.</summary>
    Success = 0xFF,
}
