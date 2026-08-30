// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Microsoft.Extensions.Logging;
using Periphery.Bootloader;
using Periphery.FlashAnything;
using CliFrontEnd = Periphery.FlashAnything.Cli.Cli; // the namespace segment 'Cli' shadows the class name

namespace Periphery.FlashAnything.Gui;

/// <summary>
/// The single-binary dual-mode launcher: one self-contained executable that runs the FlashAnything
/// CLI when invoked from a terminal and opens the GUI when double-clicked. Both front-ends already
/// ride the same engine (<see cref="FlashAnythingService"/>) and composition, so bundling them costs
/// only the CLI's small managed code on top of a payload that already pays for the .NET runtime — the
/// runtime is not duplicated across two deployments. A branded flasher's <c>[STAThread] Main</c> is a
/// thin call to <see cref="Run"/>.
/// </summary>
/// <remarks>
/// Dispatch is purely on the arguments — never on launch context — so it is deterministic whether
/// started from a terminal or Explorer:
/// <list type="bullet">
///   <item>no args — GUI. (A bundled dual-mode binary has a window to show, so a bare run opens it;
///   listing targets is the explicit <c>list</c> command. The CLI-only build keeps its own no-args
///   default of <c>list</c> — only this combined binary routes no-args to the GUI.)</item>
///   <item><c>gui [--log-file P] [--exit-after N]</c> — explicit GUI launch carrying the GUI host's
///   own flags (<see cref="GuiHost.Run"/>); keeps the autonomous-debug-loop launch working under a
///   single binary.</item>
///   <item>anything else (<c>list</c>, <c>flash …</c>, <c>--help</c>, …) — CLI.</item>
/// </list>
/// <para>
/// <b>Windows caveat:</b> the host is a <c>WinExe</c>, so the shell does not wait for it — a prompt
/// returns immediately and CLI output prints after it (cosmetic interleave). Output itself is correct:
/// <see cref="ConsoleBridge.AttachToParentConsole"/> binds it to the launching console.
/// </para>
/// </remarks>
public static class DualModeHost
{
    /// <summary>
    /// Dispatches <paramref name="args"/> to the CLI or the GUI over the composition built by
    /// <paramref name="serviceFactory"/>. <paramref name="title"/> brands the window / CLI banner;
    /// <paramref name="toolCommand"/> is the CLI command name (usage + logger category);
    /// <paramref name="banner"/> is the one-line product description shown by <c>--help</c>.
    /// </summary>
    public static int Run(
        Func<ILogger?, BootloaderEntryOptions?, FlashAnythingService> serviceFactory,
        string title, string toolCommand, string banner, string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // No args -> GUI; an explicit `gui` verb -> GUI with the host's own launch flags. Both before
        // the CLI path, so GUI launches never touch the console.
        if (args.Length == 0)
            return GuiHost.Run(serviceFactory, title, args);
        if (string.Equals(args[0], "gui", StringComparison.OrdinalIgnoreCase))
            return GuiHost.Run(serviceFactory, title, args[1..]);

        // CLI path: bind output to the launching terminal (a WinExe has no console of its own on
        // Windows), then run the parse-and-dispatch loop.
        ConsoleBridge.AttachToParentConsole();
        return CliFrontEnd.RunAsync(serviceFactory, toolCommand, banner, args).GetAwaiter().GetResult();
    }
}
