// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Firmware;

/// <summary>
/// Thrown when a firmware file's format cannot be handled: an unrecognized extension, or a
/// content/extension mismatch caught by the brick-guard (e.g. Intel HEX text in a <c>.bin</c>).
/// Raised up front, before any byte reaches a device.
/// </summary>
public sealed class FirmwareFormatException : Exception
{
    public FirmwareFormatException(string message) : base(message) { }
    public FirmwareFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
