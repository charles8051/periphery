// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Periphery.FlashAnything.Cli;
using Periphery.Treehopper;

namespace Periphery.Treehopper.Flasher.Cli;

/// <summary>
/// The <c>reboot</c> verb: issue <see cref="TreehopperBoard.RebootAsync"/> — wire opcode
/// <c>0x0C</c>, the port of the original SDK's <c>TreehopperUsb.Reboot()</c> — and report whether
/// the device actually drops off USB and re-enumerates.
/// </summary>
/// <remarks>
/// <para>
/// This is the MCU firmware reset, distinct from <c>EnterBootloader</c> (<c>0x0D</c>). Host-side
/// rungs (PnP disable/enable, USB port cycle) only re-enumerate the host's view of a device and
/// cannot reset board firmware, so a wedged firmware endpoint survives them. A firmware reboot is
/// the only reset that can clear that, and it was the one rung no shipped tool exposed.
/// </para>
/// <para>
/// Its practical value is as a per-board health probe: a completed write versus a bulk-endpoint
/// timeout is a fast, zero-downtime discriminator between "flashable" and "needs a physical
/// replug". It cannot <em>rescue</em> a wedged board — <c>0x0C</c> goes to the same wedged endpoint.
/// </para>
/// <para>
/// <b><c>0x0C</c> does re-enumerate the board.</b> This verb used to say otherwise, because it
/// polled the device tree every 500 ms while the board is only absent for a couple of hundred
/// milliseconds — a working reset landed inside one sampling interval and was reported as
/// <c>NO EFFECT</c>. The watch is now the OS's own device notifications
/// (<see cref="Devices.Watch"/>, the same stream <c>periphery devices watch</c> uses), so the
/// transient cannot be sampled past, and the verdict reports the measured absence.
/// </para>
/// </remarks>
internal static class RebootVerb
{
    /// <summary>The verb, as the shared CLI routes and documents it.</summary>
    public static CliVerb Create() =>
        new("reboot", "reboot (--target <id> | --all --yes)",
            "Reset board firmware (0x0C) and report whether the board drops off USB and returns.",
            RunAsync)
        {
            OptionsHelp =
                """
                REBOOT OPTIONS
                  -t, --target <id>     Reboot one board (also accepts --target=<id>).
                      --all             Reboot every detected board. Requires --yes.
                  -y, --yes             Confirm. Required for --all.

                  Rebooting resets board firmware and drops the USB link, so any application
                  currently driving the board loses it. Do not run against a live workload.
                """,
        };

    private static async Task<int> RunAsync(string[] args, ILoggerFactory? loggerFactory, CancellationToken ct)
    {
        var parsed = VerbTargeting.Parse(
            args, "reboot",
            "--all reboots EVERY detected board, resetting firmware and dropping each USB link.",
            HelpText);
        if (parsed.ExitCode is { } usageExit) return usageExit;

        var boards = await TreehopperBoard.EnumerateAsync(ct);
        if (boards.Count == 0) { Console.Error.WriteLine("No Treehopper boards found."); return ExitCodes.NoTarget; }

        var chosen = VerbTargeting.Select(boards, parsed, "reboot", out int selectionExit);
        if (chosen is null) return selectionExit;

        int failed = 0;
        foreach (var info in chosen)
        {
            Console.WriteLine($"Rebooting {info.Name} ({info.Id}) ...");

            var observed = await RebootAndWatchAsync(info, loggerFactory, ct);
            if (observed is not { } observation) { failed++; continue; }

            string verdict = BoardReboot.Summarize(observation, WatchBudget);
            if (BoardReboot.Classify(observation) is RebootOutcome.Rebooted) Console.WriteLine($"  {verdict}");
            else { Console.Error.WriteLine($"  {verdict}"); failed++; }
        }

        return failed == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    private static void HelpText()
    {
        Console.WriteLine("treehopper-flash reboot (--target <id> | --all --yes)");
        Console.WriteLine("  Issue the board firmware reboot (wire opcode 0x0C) and report whether");
        Console.WriteLine("  the device actually drops off USB and re-enumerates.");
        Console.WriteLine();
        Console.WriteLine("  -t, --target <id>  Reboot one board (also accepts --target=<id>).");
        Console.WriteLine("      --all          Reboot every detected board. Requires --yes.");
        Console.WriteLine("  -y, --yes          Confirm. Required for --all.");
        Console.WriteLine("  -v, --verbose      Log the board open + transaction detail to stderr.");
        Console.WriteLine();
        Console.WriteLine("  Rebooting resets board firmware and drops the USB link, so any application");
        Console.WriteLine("  currently driving the board loses it. Do not run against a live workload.");
        Console.WriteLine();
        Console.WriteLine("  Cannot rescue a WEDGED board - 0x0C travels over the endpoint that is stuck.");
        Console.WriteLine("  Use `treehopper-flash rescue` for that.");
        Console.WriteLine();
        Console.WriteLine("  Exit: 0 ok  1 a reboot failed or had no effect  2 usage  4 no matching board");
    }

    /// <summary>How long to wait for the board to leave the bus and come back.</summary>
    /// <remarks>
    /// Generous next to the ~0.2s a reboot actually takes, and it only costs that much when the
    /// board does <em>not</em> come back: a successful watch returns the moment both edges are in.
    /// </remarks>
    private static readonly TimeSpan WatchBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Writes <c>0x0C</c> to one board and watches the OS device notifications for the drop and the
    /// return. Returns the folded observation, or <c>null</c> when the write itself failed (already
    /// reported).
    /// </summary>
    /// <remarks>
    /// This is the whole point of the verb: proving whether <c>0x0C</c> reaches the firmware. The
    /// subscription, clock and wait live in <see cref="BoardTransitionWatch"/>, shared with the
    /// <c>rescue</c> verb; what is specific here is the write itself and how a failed write reads.
    /// </remarks>
    private static async Task<RebootObservation?> RebootAndWatchAsync(
        DeviceInfo info, ILoggerFactory? loggerFactory, CancellationToken ct)
    {
        try
        {
            return await BoardTransitionWatch.WatchAsync(info, async token =>
            {
                var board = await TreehopperBoard.OpenAsync(info, token, loggerFactory);
                try
                {
                    await board.RebootAsync(token);
                    Console.WriteLine("  reboot command sent (0x0C)");
                }
                finally
                {
                    // The USB link drops as the board reboots, so a close fault here is expected.
                    try { await board.DisposeAsync(); } catch { /* expected as the link drops */ }
                }
            }, WatchBudget, ct);
        }
        catch (OperationCanceledException)
        {
            // Never fold cancellation into a device failure - let it propagate.
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAILED - {ex.GetBaseException().Message}");
            return null;
        }
    }
}
