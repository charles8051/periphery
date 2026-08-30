// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// The <see cref="IBootloaderProvider"/> for the STM32 system USB DFU bootloader
/// (VID 0x0483 / PID 0xDF11). Register it with a <see cref="BootloaderRegistry"/> so the
/// FlashAnything dispatcher can resolve and open STM32 DFU targets.
/// </summary>
public sealed class Stm32UsbBootloaderProvider : IBootloaderProvider
{
    /// <summary>STMicroelectronics USB vendor id.</summary>
    public static readonly HardwareId VendorId = new(0x0483);

    /// <summary>STM32 system DFU bootloader product id.</summary>
    public static readonly HardwareId ProductId = new(0xDF11);

    /// <inheritdoc />
    public string Name => "STM32 USB DFU";

    /// <inheritdoc />
    public bool CanHandle(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.VendorId == VendorId && device.ProductId == ProductId;
    }

    /// <inheritdoc />
    public async Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        => await Stm32DfuProgrammer.OpenAsync(device, interfaceNumber: 0, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public IdentificationMode Identification => IdentificationMode.Passive; // 0483:DF11 (USB VID/PID) is the target
}
