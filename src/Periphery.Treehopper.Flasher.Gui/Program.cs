// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Periphery.FlashAnything.Gui;
using Periphery.Treehopper.Flasher;

namespace Periphery.Treehopper.Flasher.Gui;

// Treehopper Flasher (ADR-0063 DEC-006): a single self-contained binary that is both front-end over
// the curated Treehopper composition. Invoked from a terminal it runs the FlashAnything CLI; double-
// clicked it opens the GUI. A thin Main over the shared DualModeHost.Run; no UI or CLI is duplicated.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        DualModeHost.Run(
            // Concurrent-by-default: EFM8 boards flash at once (hardware-verified safe; each correlated to
            // its own USB port). See CreateService; pass allowConcurrentEfm8Flash: false to force serialize.
            (logger, entryOptions) => TreehopperFlasher.CreateService(logger, entryOptions),
            TreehopperFlasher.Name,
            TreehopperFlasher.ToolCommand,
            $"flash firmware to Treehopper boards ({TreehopperFlasher.Name})",
            args);
}
