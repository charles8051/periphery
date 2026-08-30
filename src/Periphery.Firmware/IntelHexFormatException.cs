// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Firmware;

/// <summary>
/// Thrown when an Intel HEX input is not well-formed (a record without the
/// <c>:</c> start mark, a bad byte count, a non-hex digit, a checksum mismatch,
/// an unknown record type, or a missing end-of-file record).
/// </summary>
/// <remarks>
/// Parsing is total and happens up front, before any boot record is generated —
/// a malformed HEX can never produce a partial or wrong flash image.
/// </remarks>
public sealed class IntelHexFormatException : Exception
{
    public IntelHexFormatException(string message) : base(message) { }
    public IntelHexFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
