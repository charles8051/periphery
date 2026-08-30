// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// Thrown when a boot-record stream is not a well-formed sequence of
/// <c>$</c>-framed EFM8 records (bad start byte, declared length overruns the
/// stream, empty stream, or a zero-length record carrying no command byte).
/// </summary>
/// <remarks>
/// Parsing is total and happens up front, before any byte is written to the
/// device — a malformed stream can never produce a partial flash.
/// </remarks>
public sealed class Efm8BootFormatException : Exception
{
    public Efm8BootFormatException(string message) : base(message) { }
    public Efm8BootFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
