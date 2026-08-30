// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
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
    return new FlashAnythingService(registry, logger: logger, entryOptions: entryOptions);
}
