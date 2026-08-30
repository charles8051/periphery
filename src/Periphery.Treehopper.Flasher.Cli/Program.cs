// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.FlashAnything.Cli;
using Periphery.Treehopper.Flasher;
using Periphery.Treehopper.Flasher.Cli;

// Treehopper Flasher CLI (ADR-0063 DEC-006): the same FlashAnything CLI front-end (Cli.RunAsync)
// over a curated Treehopper-only composition + branding. The proof that a device-specific flasher
// is a thin composition, not a fork.
//
// Two verbs are Treehopper's own and cannot live in the shared FlashAnything core, which is
// device-agnostic and also fronts STM32. Both are *routed* by that core rather than forking it:
// each is a CliVerb, so it is dispatched and documented in --help alongside list/flash/autoflash
// while owning its own parsing, output, and exit code.
return await Cli.RunAsync(
    // Concurrent-by-default: the composition flashes several EFM8 boards at once (allowConcurrentEfm8Flash
    // defaults true) — hardware-verified safe, each board correlated to its own USB port by ByLocationPath.
    // See TreehopperFlasher.CreateService; pass false there to force serialize.
    (logger, entryOptions) => TreehopperFlasher.CreateService(logger, entryOptions),
    TreehopperFlasher.ToolCommand,
    $"flash firmware to Treehopper boards ({TreehopperFlasher.Name})",
    args,
    verbs:
    [
        RenameVerb.Create(TreehopperFlasher.ToolCommand),
        RebootVerb.Create(),
        // The out-of-band counterpart to reboot (ADR-0075): the one reset that still lands when
        // the board's foreground has stopped and 0x0C can no longer be delivered.
        RescueVerb.Create(),
        // Independent confirmation of what's actually on a board, without reflashing it — a flash's
        // own embedded verify is not proof (periphery#246: verified: false), so this runs a Verify
        // check in a separate, later bootloader session instead.
        VerifyVerb.Create(),
    ]);
