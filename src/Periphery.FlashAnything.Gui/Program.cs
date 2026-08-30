// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Microsoft.Extensions.Logging;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Bootloader.Stm32.Usb;
using Periphery.FlashAnything;

namespace Periphery.FlashAnything.Gui;

// The generic "flash anything that is already in bootloader mode" GUI: a thin [STAThread] Main over
// the shared GUI toolkit (GuiHost.Run). It composes only bootloader-mode flashers; app-mode device
// flashers are siblings over the same toolkit + engine - not registered here.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => GuiHost.Run(DefaultService, "FlashAnything", args);

    private static FlashAnythingService DefaultService(ILogger? logger, BootloaderEntryOptions? entryOptions)
    {
        var registry = new BootloaderRegistry();
        registry.Register(new Stm32UsbBootloaderProvider()); // STM32 USB DFU (0483:DF11)
        registry.Register(new Efm8UsbBootloaderProvider());  // EFM8 USB-HID bootloader (10C4:EAC9)
        return new FlashAnythingService(registry, logger: logger, entryOptions: entryOptions);
    }
}
