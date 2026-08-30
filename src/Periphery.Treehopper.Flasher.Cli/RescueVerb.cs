// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.FlashAnything.Cli;
using Periphery.Treehopper;

namespace Periphery.Treehopper.Flasher.Cli;

/// <summary>
/// The <c>rescue</c> verb: issue the EP0 vendor rescue reset
/// (<see cref="TreehopperBoard.RescueResetAsync(DeviceInfo, System.Threading.CancellationToken, ILoggerFactory?)"/>)
/// and report whether the board actually re-enumerated. The recovery of last resort before a
/// physical replug.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use this when <c>reboot</c> cannot get through.</b> <c>reboot</c>'s <c>0x0C</c> travels over
/// the peripheral-config bulk endpoint, which the firmware re-arms only from its foreground
/// superloop; if that superloop has stopped, the command goes to the very endpoint that is wedged.
/// EP0 is serviced from the device's USB ISR, so it stays reachable in exactly that state
/// (ADR-0075).
/// </para>
/// <para>
/// <b>This does not open the board.</b> Opening one reconciles its configuration over the same
/// wedged endpoint, so a verb that opened the board first would fail on every board it exists for.
/// </para>
/// <para>
/// <b>The request cannot be confirmed from the transfer.</b> It faults whether the board reset or
/// the firmware never implemented it, so the watch for re-enumeration is the only evidence, and a
/// board that never leaves the bus most likely has firmware predating the handler rather than
/// broken hardware.
/// </para>
/// </remarks>
internal static class RescueVerb
{
    /// <summary>The verb, as the shared CLI routes and documents it.</summary>
    public static CliVerb Create() =>
        new("rescue", "rescue (--target <id> | --all --yes)",
            "Reset a wedged board out-of-band over EP0, when reboot (0x0C) cannot be delivered.",
            RunAsync)
        {
            OptionsHelp =
                """
                RESCUE OPTIONS
                  -t, --target <id>     Rescue one board (also accepts --target=<id>).
                      --all             Rescue every detected board. Requires --yes.
                  -y, --yes             Confirm. Required for --all.

                  Rescue resets the MCU from its USB interrupt handler, without the application
                  firmware's participation, and drops the USB link. Any application driving the
                  board loses it. Do not run against a live workload.

                  Requires firmware carrying the rescue handler; older firmware ignores the
                  request silently, which reads as NO RESCUE.
                """,
        };

    /// <summary>How long to wait for the board to leave the bus and come back.</summary>
    private static readonly TimeSpan WatchBudget = TimeSpan.FromSeconds(20);

    private static async Task<int> RunAsync(string[] args, ILoggerFactory? loggerFactory, CancellationToken ct)
    {
        var parsed = VerbTargeting.Parse(
            args, "rescue",
            "--all rescues EVERY detected board, resetting each MCU and dropping each USB link.",
            HelpText);
        if (parsed.ExitCode is { } usageExit) return usageExit;

        var boards = await TreehopperBoard.EnumerateAsync(ct);
        if (boards.Count == 0) { Console.Error.WriteLine("No Treehopper boards found."); return ExitCodes.NoTarget; }

        var chosen = VerbTargeting.Select(boards, parsed, "rescue", out int selectionExit);
        if (chosen is null) return selectionExit;

        int failed = 0;
        foreach (var info in chosen)
        {
            Console.WriteLine($"Rescuing {info.Name} ({info.Id}) ...");

            RebootObservation observation;
            try
            {
                observation = await BoardTransitionWatch.WatchAsync(info, async token =>
                {
                    // Static, NOT TreehopperBoard.OpenAsync + the instance method: opening the board
                    // writes to the endpoint a wedged board is not draining (ADR-0075).
                    await TreehopperBoard.RescueResetAsync(info, token, loggerFactory);
                    Console.WriteLine("  rescue request sent (EP0 vendor reset)");
                }, WatchBudget, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The USB device could not be opened at all, so the request never left the host.
                // Distinct from a request that went out and cannot be confirmed.
                Console.Error.WriteLine($"  FAILED - {ex.GetBaseException().Message}");
                failed++;
                continue;
            }

            string verdict = BoardReboot.SummarizeRescue(observation, WatchBudget);
            if (BoardReboot.Classify(observation) is RebootOutcome.Rebooted) Console.WriteLine($"  {verdict}");
            else { Console.Error.WriteLine($"  {verdict}"); failed++; }
        }

        return failed == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    private static void HelpText()
    {
        Console.WriteLine("treehopper-flash rescue (--target <id> | --all --yes)");
        Console.WriteLine("  Reset a board out-of-band over EP0 and report whether it re-enumerated.");
        Console.WriteLine("  For a board whose foreground has stopped, where reboot (0x0C) cannot be");
        Console.WriteLine("  delivered because it travels over the wedged endpoint.");
        Console.WriteLine();
        Console.WriteLine("  -t, --target <id>  Rescue one board (also accepts --target=<id>).");
        Console.WriteLine("      --all          Rescue every detected board. Requires --yes.");
        Console.WriteLine("  -y, --yes          Confirm. Required for --all.");
        Console.WriteLine("  -v, --verbose      Log the USB open + transfer detail to stderr.");
        Console.WriteLine();
        Console.WriteLine("  Requires firmware carrying the rescue handler; older firmware ignores");
        Console.WriteLine("  the request, which reads as NO RESCUE.");
        Console.WriteLine();
        Console.WriteLine("  Exit: 0 ok  1 a rescue failed or had no effect  2 usage  4 no matching board");
    }
}
