// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// DFU 1.1 status code (the <c>bStatus</c> byte of a GETSTATUS response). <see cref="Ok"/>
/// means the previous request completed; anything else is an error the host must clear
/// with DFU_CLRSTATUS. STM32 (AN3156) chiefly uses <see cref="ErrTarget"/> (address not
/// allowed) and <see cref="ErrVendor"/> (read/write protection active).
/// </summary>
public enum DfuStatusCode : byte
{
    Ok = 0x00,
    ErrTarget = 0x01,
    ErrFile = 0x02,
    ErrWrite = 0x03,
    ErrErase = 0x04,
    ErrCheckErased = 0x05,
    ErrProg = 0x06,
    ErrVerify = 0x07,
    ErrAddress = 0x08,
    ErrNotDone = 0x09,
    ErrFirmware = 0x0A,
    ErrVendor = 0x0B,
    ErrUsbReset = 0x0C,
    ErrPor = 0x0D,
    ErrUnknown = 0x0E,
    ErrStalledPkt = 0x0F,
}
