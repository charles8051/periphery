// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// Thrown for upload failures that are not a clean protocol-level reject — a safety
/// gate refusing to write (wrong device), or an IO fault mid-stream. A protocol-level
/// non-acknowledge reply is reported via <see cref="Efm8UploadResult"/> instead, not
/// thrown.
/// </summary>
public sealed class Efm8BootloaderException : Exception
{
    public Efm8BootloaderException(string message) : base(message) { }
    public Efm8BootloaderException(string message, Exception innerException)
        : base(message, innerException) { }
}
