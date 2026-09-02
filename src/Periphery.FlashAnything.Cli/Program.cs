// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Bootloader.Stm32.Serial;
using Periphery.Bootloader.Stm32.Usb;
using Periphery.FlashAnything;
using Periphery.FlashAnything.Cli;

// FlashAnything CLI (ADR-0061 north star): the generic "flash anything that is already in bootloader
// mode" tool, a thin Main over the shared Cli toolkit. It composes only bootloader-mode flashers;
// app-mode devices (which need a software reboot-into-bootloader call) belong to device-specific
// flasher products that are siblings over the same toolkit + engine - not registered here.
return await Cli.RunAsync(
    DefaultService,
    "flashany",
    "flash firmware to devices in bootloader mode (Periphery FlashAnything)",
    args);

// entryOptions is forwarded rather than dropped: this composition registers no bootloader entries
// today, so it changes nothing - but the day one is added, the CLI's --bootloader-timeout works.
static FlashAnythingService DefaultService(ILogger? logger, BootloaderEntryOptions? entryOptions)
{
    var registry = new BootloaderRegistry();
    registry.Register(new Stm32UsbBootloaderProvider()); // STM32 USB DFU (0483:DF11)
    registry.Register(new Efm8UsbBootloaderProvider());  // EFM8 USB-HID bootloader (10C4:EAC9)

    // Last, and the order is load-bearing: this one claims any device with a serial port name,
    // because a USB-serial bridge's VID/PID never names the STM32 behind it. Registering it
    // ahead of a VID/PID-matched provider would let it swallow that provider's device. Its
    // IdentificationMode.Probe keeps it out of unattended autoflash either way.
    registry.Register(new Stm32SerialBootloaderProvider()); // STM32 UART system bootloader (AN3155)
    return new FlashAnythingService(registry, logger: logger, entryOptions: entryOptions);
}
