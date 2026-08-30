// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// DFU 1.1 device state (the <c>bState</c> byte of a GETSTATUS response, §6.1.2 of the
/// USB DFU spec). Generic DFU — will graduate to a <c>Periphery.Bootloader.Dfu</c> package
/// when a second DFU consumer (e.g. ESP32-S2/S3) appears (ADR-0061 DEC-005).
/// </summary>
public enum DfuState : byte
{
    AppIdle = 0,
    AppDetach = 1,
    DfuIdle = 2,
    DfuDnloadSync = 3,
    DfuDnbusy = 4,
    DfuDnloadIdle = 5,
    DfuManifestSync = 6,
    DfuManifest = 7,
    DfuManifestWaitReset = 8,
    DfuUploadIdle = 9,
    DfuError = 10,
}
