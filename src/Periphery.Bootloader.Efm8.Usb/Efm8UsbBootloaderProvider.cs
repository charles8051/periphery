// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The <see cref="IBootloaderProvider"/> for the SiLabs EFM8 factory USB-HID bootloader
/// (VID <c>0x10C4</c> / PID <c>0xEAC9</c>) — the identity every EFM8 part re-enumerates as in
/// bootloader mode. Register it with a <see cref="BootloaderRegistry"/> so the FlashAnything
/// dispatcher can resolve and open EFM8 targets (and, with a Treehopper <c>IBootloaderEntry</c>,
/// flash a Treehopper end-to-end).
/// </summary>
public sealed class Efm8UsbBootloaderProvider : IBootloaderProvider
{
    /// <summary>SiLabs USB vendor id.</summary>
    public static readonly HardwareId VendorId = new(0x10C4);

    /// <summary>EFM8 factory USB-HID bootloader product id.</summary>
    public static readonly HardwareId ProductId = new(0xEAC9);

    /// <inheritdoc />
    public string Name => "EFM8 USB-HID bootloader";

    /// <inheritdoc />
    public bool CanHandle(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        // Match only the HID-bus interface, not the USB-bus parent that shares the same VID/PID.
        // A USB-HID device enumerates as two nodes (the USB device node and its HID child); both
        // carry VID_10C4&PID_EAC9, so a VID/PID-only match would surface the bootloader twice — a
        // duplicate target row, and a USB-parent "target" that CreateFile can't open as HID. The
        // HID-bus child is the only node Efm8HidProgrammer can open. Mirrors HidBatteryEnricher's gate.
        return device.VendorId == VendorId && device.ProductId == ProductId
            && device is { Category: DeviceCategory.Hid, BusType: BusType.HID };
    }

    /// <inheritdoc />
    public async Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        => await Efm8HidProgrammer.OpenAsync(device, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public IdentificationMode Identification => IdentificationMode.Passive; // 10C4:EAC9 (USB VID/PID) is the target
}
