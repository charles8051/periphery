// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// Explicit acknowledgement that an upload erases and rewrites device firmware.
/// A required, non-defaulted argument to every destructive entry point so a reflash
/// cannot be triggered by an accidental call — the caller must name
/// <see cref="ConfirmEraseAndReflash"/> at the call site.
/// </summary>
public enum Efm8FlashConfirmation
{
    /// <summary>Default / unset. Rejected by every upload entry point.</summary>
    Unconfirmed = 0,

    /// <summary>
    /// The caller understands this erases the device's application flash and writes
    /// the supplied image in its place. Required to proceed.
    /// </summary>
    ConfirmEraseAndReflash = 1,
}
