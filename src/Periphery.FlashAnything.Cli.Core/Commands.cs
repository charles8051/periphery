// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Periphery.Bootloader;
using Periphery.FlashAnything;

namespace Periphery.FlashAnything.Cli;

/// <summary>
/// The CLI command handlers (composition-agnostic): each runs against a service built by the
/// caller-supplied factory and renders the result. The composition — which providers / entries /
/// converters — lives in each front-end's entry point (DEC-006), not in this shared toolkit.
/// </summary>
internal static class Commands
{

    public static async Task<int> ListAsync(
        Func<ILogger?, BootloaderEntryOptions?, FlashAnythingService> serviceFactory, Parsed p, ILogger? logger, CancellationToken ct)
    {
        await using var service = serviceFactory(logger, p.EntryOptions);
        await service.RefreshAsync(ct).ConfigureAwait(false);

        var targets = service.State.Targets;
        if (targets.Length == 0)
        {
            Console.WriteLine("No flashable targets detected.");
            Console.WriteLine("(Connect a device in bootloader mode, e.g. an STM32 in DFU mode: 0483:DF11.)");
            return ExitCodes.Success;
        }

        Console.WriteLine($"{targets.Length} flashable target(s):");
        foreach (var t in targets)
        {
            string mode = t.RebootsToFlash ? "  (application - reboots to flash)" : "";
            Console.WriteLine($"  {t.Id}  {t.DisplayName} [{t.ProviderName}]{mode}");
        }
        return ExitCodes.Success;
    }

    public static async Task<int> FlashAsync(
        Func<ILogger?, BootloaderEntryOptions?, FlashAnythingService> serviceFactory, Parsed p, ILogger? logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.File))
        {
            Console.Error.WriteLine("flash requires --file <path>.");
            return ExitCodes.Usage;
        }
        if (!File.Exists(p.File))
        {
            Console.Error.WriteLine($"Firmware file not found: {p.File}");
            return ExitCodes.NoImage;
        }

        await using var service = serviceFactory(logger, p.EntryOptions);
        await service.RefreshAsync(ct).ConfigureAwait(false);
        var state = service.State;

        if (state.Targets.Length == 0)
        {
            Console.Error.WriteLine("No flashable target detected. Connect a device in bootloader mode (e.g. STM32 DFU 0483:DF11).");
            return ExitCodes.NoTarget;
        }

        IReadOnlyList<FlashTargetView> targets;
        if (p.Target is { } targetId)
        {
            // t.Id is a DeviceId, so this compares OrdinalIgnoreCase: a --target the
            // operator copied before a reset still matches after it (issue #231).
            targets = state.Targets.Where(t => t.Id == targetId).ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"Target '{targetId}' not found among detected targets.");
                return ExitCodes.NoTarget;
            }
        }
        else if (p.All)
        {
            targets = state.Targets;
        }
        else if (state.Targets.Length == 1)
        {
            targets = new[] { state.Targets[0] };
        }
        else
        {
            Console.Error.WriteLine($"{state.Targets.Length} targets detected; use --target <id> or --all:");
            foreach (var t in state.Targets) Console.Error.WriteLine($"  {t.Id}  {t.DisplayName}");
            return ExitCodes.Usage;
        }

        // Base for a raw .bin (override with --base); ignored for .hex, which carries its own.
        uint baseAddress = p.BaseAddress ?? 0x08000000u;
        var options = new FlashOptions
        {
            LeaveAfterFlash = !p.NoLeave,
            Verify = !p.NoVerify,
        };

        var fileInfo = new FileInfo(p.File);
        Console.WriteLine($"Firmware : {p.File} ({fileInfo.Length} bytes)");
        Console.WriteLine($"Base     : 0x{baseAddress:X8}{(p.BaseAddress is null ? " (default)" : "")}  [raw .bin only]");
        Console.WriteLine($"Targets  : {targets.Count}");
        foreach (var t in targets) Console.WriteLine($"  {t.DisplayName} [{t.ProviderName}]");

        // Parse + validate the image up front (format detect + brick-guard) so a bad file
        // fails loudly here - even on a dry run - instead of as a vague per-target error.
        await service.DispatchAsync(new AppIntent.LoadFirmware(p.File, baseAddress), ct).ConfigureAwait(false);
        if (service.State.Firmware is not { } firmware)
        {
            Console.Error.WriteLine(service.State.FirmwareError ?? "Failed to load firmware.");
            return ExitCodes.NoImage;
        }
        Console.WriteLine($"Image    : {firmware.Size} bytes to write");

        if (!p.Yes)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run - nothing written. Re-run with --yes to flash.");
            return ExitCodes.Success;
        }

        Console.WriteLine();
        int failed;
        if (targets.Count == 1)
        {
            // One target: flash it directly.
            var t = targets[0];
            Console.WriteLine($"Flashing {t.DisplayName}...");
            bool ok = await service.FlashAsync(t.Id, options, ct).ConfigureAwait(false);
            var final = service.State.Find(t.Id);
            if (ok)
            {
                Console.WriteLine($"  OK - {final?.Message ?? "flashed"}");
                failed = 0;
            }
            else
            {
                Console.Error.WriteLine($"  FAILED - {final?.LastError ?? final?.Message ?? "unknown error"}");
                failed = 1;
            }
        }
        else
        {
            // --all: flash every target concurrently (bounded by the service's flash concurrency),
            // streaming each board's terminal outcome as it lands. Boards finish interleaved, so a
            // small lock keeps each line atomic and prints each id exactly once.
            Console.WriteLine($"Flashing {targets.Count} targets in parallel...");
            var printed = new HashSet<string>();
            var printLock = new object();
            void OnState(AppState s)
            {
                lock (printLock)
                {
                    foreach (var v in s.Targets)
                    {
                        if (v.Stage is FlashStage.Flashed or FlashStage.Failed && printed.Add(v.Id))
                        {
                            if (v.Stage == FlashStage.Flashed)
                                Console.WriteLine($"  OK     {v.DisplayName} - {v.Message ?? "flashed"}");
                            else
                                Console.Error.WriteLine($"  FAILED {v.DisplayName} - {v.LastError ?? v.Message ?? "unknown error"}");
                        }
                    }
                }
            }

            service.StateChanged += OnState;
            try
            {
                var summary = await service.FlashAllAsync(options, ct).ConfigureAwait(false);
                failed = summary.Failed;
            }
            finally
            {
                service.StateChanged -= OnState;
            }
        }

        return failed == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    public static async Task<int> AutoflashAsync(
        Func<ILogger?, BootloaderEntryOptions?, FlashAnythingService> serviceFactory, Parsed p, ILogger? logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.File))
        {
            Console.Error.WriteLine("autoflash requires --file <path>.");
            return ExitCodes.Usage;
        }
        if (!File.Exists(p.File))
        {
            Console.Error.WriteLine($"Firmware file not found: {p.File}");
            return ExitCodes.NoImage;
        }

        await using var service = serviceFactory(logger, p.EntryOptions);
        var families = service.KnownFamilies;

        // Resolve the family: an explicit --family must name a registered provider; otherwise
        // default to the sole provider (error if there are several to choose from).
        string family;
        if (!string.IsNullOrWhiteSpace(p.Family))
        {
            if (!families.Contains(p.Family))
            {
                Console.Error.WriteLine($"Unknown family '{p.Family}'. Known: {string.Join(", ", families)}");
                return ExitCodes.Usage;
            }
            family = p.Family;
        }
        else if (families.Count == 1)
        {
            family = families[0];
        }
        else
        {
            Console.Error.WriteLine($"Several families available; pass --family <name>: {string.Join(", ", families)}");
            return ExitCodes.Usage;
        }

        uint baseAddress = p.BaseAddress ?? 0x08000000u;
        var options = new FlashOptions { LeaveAfterFlash = !p.NoLeave, Verify = !p.NoVerify };

        // Load + validate the image up front (format detect + brick-guard).
        await service.DispatchAsync(new AppIntent.LoadFirmware(p.File, baseAddress), ct).ConfigureAwait(false);
        if (service.State.Firmware is not { } firmware)
        {
            Console.Error.WriteLine(service.State.FirmwareError ?? "Failed to load firmware.");
            return ExitCodes.NoImage;
        }

        Console.WriteLine("Autoflash");
        Console.WriteLine($"  Firmware : {p.File} ({firmware.Size} bytes)");
        Console.WriteLine($"  Family   : {family}");

        if (!p.Yes)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run - would arm and flash matching devices on plug-in. Re-run with --yes to arm.");
            return ExitCodes.Success;
        }

        // Stream per-device outcomes from the session tally's audit list (newest last).
        int printed = 0;
        var printLock = new object();
        service.StateChanged += s =>
        {
            lock (printLock)
            {
                var audit = s.AutoflashTally.Audit;
                for (; printed < audit.Length; printed++)
                    Console.WriteLine($"  {audit[printed]}");
            }
        };

        await service.RefreshAsync(ct).ConfigureAwait(false); // start the watcher
        await service.DispatchAsync(new AppIntent.ArmAutoflash(family, options), ct).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("Armed. Plug in devices to flash them automatically. Press Ctrl+C to stop.");
        Console.WriteLine();

        try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Ctrl+C */ }

        var tally = service.State.AutoflashTally;
        Console.WriteLine();
        Console.WriteLine($"Stopped. Flashed {tally.Flashed}, failed {tally.Failed}, skipped {tally.Skipped}.");
        return tally.Failed == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }
}
