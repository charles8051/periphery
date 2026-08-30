// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// The DFU class-specific request codes (<c>bRequest</c>), AN3156 Table 2 / DFU 1.1.
/// Carried on the USB control endpoint with <c>bmRequestType</c> = 0x21 (host→device) or
/// 0xA1 (device→host), class, recipient = interface.
/// </summary>
internal enum DfuRequest : byte
{
    Detach = 0x00,
    Dnload = 0x01,
    Upload = 0x02,
    GetStatus = 0x03,
    ClrStatus = 0x04,
    GetState = 0x05,
    Abort = 0x06,
}
