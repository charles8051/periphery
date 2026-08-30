// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Firmware;

/// <summary>
/// Thrown when an ELF input is not a well-formed, loadable ELF: a bad magic, an
/// unsupported class/data-encoding byte, a truncated header or program-header table,
/// a segment whose data runs past the end of the file, a load address outside the
/// 32-bit space Periphery flashes, or an ELF that carries nothing to flash (no
/// <c>PT_LOAD</c> segment with file data — a relocatable object or a debug-only file).
/// </summary>
/// <remarks>
/// Parsing is total and happens up front, before any byte reaches a device — a
/// malformed ELF can never produce a partial or wrong flash image (ADR-0052).
/// </remarks>
public sealed class ElfFormatException : Exception
{
    public ElfFormatException(string message) : base(message) { }
    public ElfFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
