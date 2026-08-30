// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;

namespace Periphery.Treehopper;

/// <summary>
/// Raised when a Treehopper board operation fails. The
/// <see cref="Exception.InnerException"/> carries the underlying transport error
/// (typically a <c>Periphery.Usb.UsbException</c>).
/// </summary>
public class TreehopperException : IOException
{
    public TreehopperException(string message)
        : base(message) { }

    public TreehopperException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// The response endpoint's contents can no longer be trusted to belong to the command that
/// asked for them, so the board refuses further request/response traffic (#263 item 3).
/// </summary>
/// <remarks>
/// Raised when a previous transaction expecting a response did not consume one — it timed
/// out, faulted, or was cancelled after the command had already gone out. The device may
/// still deliver that response, and the Treehopper wire protocol carries no sequence or
/// correlation field, so the next read would take those bytes as its own reply. There is
/// nothing to distinguish a stale response from a fresh one, and no read-back command to
/// resynchronise with (see <c>TreehopperBoard.ResyncAsync</c>, which re-asserts config but
/// cannot drain this pipe).
/// <para>
/// The connection is the unit of recovery: dispose this board and re-open it. A fresh
/// handle starts with an empty pipe and re-applies configuration from blank, which is what
/// the per-connection <c>_applied</c> invariant already assumes. Config writes (reconciles,
/// LED flushes) are unaffected and keep working — they neither read this endpoint nor put
/// anything on it.
/// </para>
/// </remarks>
public sealed class TreehopperDesyncException : TreehopperException
{
    public TreehopperDesyncException(string message)
        : base(message) { }
}

/// <summary>An I2C transaction completed but the device reported a bus-level error.</summary>
public sealed class TreehopperI2cException : TreehopperException
{
    /// <summary>The address the transaction targeted.</summary>
    public byte Address { get; }

    /// <summary>The reported transfer error.</summary>
    public I2cTransferError Error { get; }

    public TreehopperI2cException(byte address, I2cTransferError error)
        : base($"I2C transaction to 0x{address:X2} failed: {error}.")
    {
        Address = address;
        Error = error;
    }
}
