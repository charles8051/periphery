// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.FlashAnything.Cli;

namespace Periphery.Treehopper.Flasher.Cli;

/// <summary>
/// The <c>rename</c> verb: the Treehopper Flasher's one command that is not a flash. It writes a new
/// device name to a connected board's EEPROM and reboots it so the name takes effect.
/// </summary>
/// <remarks>
/// This is the front-end half of the split — argv in, console out, exit code back. The parsing,
/// validation and board selection are <see cref="BoardRename"/>'s (pure); the USB work is
/// <see cref="BoardRenamer"/>'s (the shell). Contributed to the shared CLI as a
/// <see cref="CliVerb"/> so the toolkit stays composition-agnostic (ADR-0063 DEC-006).
/// </remarks>
internal static class RenameVerb
{
    /// <summary>The verb, as the shared CLI routes and documents it.</summary>
    public static CliVerb Create(string toolCommand) =>
        new(BoardRename.Verb, BoardRename.Usage, BoardRename.Summary,
            (args, loggerFactory, ct) => RunAsync(toolCommand, args, loggerFactory, ct))
        {
            OptionsHelp = BoardRename.OptionsHelp,
        };

    private static async Task<int> RunAsync(
        string toolCommand, string[] args, ILoggerFactory? loggerFactory, CancellationToken ct)
    {
        var parse = BoardRename.Parse(args);
        if (parse.HelpRequested)
        {
            Console.WriteLine(HelpText(toolCommand));
            return ExitCodes.Success;
        }
        if (parse.Error is not null)
        {
            Console.Error.WriteLine(parse.Error);
            Console.Error.WriteLine($"Run '{toolCommand} rename --help' for usage.");
            return ExitCodes.Usage;
        }

        var request = parse.Value!;
        var connected = await BoardRenamer.DiscoverAsync(ct).ConfigureAwait(false);
        var selection = BoardRename.Select(connected, request);
        if (selection.Error is not null)
        {
            Console.Error.WriteLine(selection.Error);
            return ExitCodes.NoTarget;
        }

        var boards = selection.Boards;
        Console.WriteLine("Rename");
        Console.WriteLine($"  New name : {request.Name}");
        Console.WriteLine($"  Reboot   : {(request.Reboot ? "yes (try to make the new name visible now)" : "no (name applies once the board re-enumerates)")}");
        Console.WriteLine($"  Boards   : {boards.Count}");
        foreach (var b in boards) Console.WriteLine($"    {BoardRename.Describe(b)}");

        if (!request.Apply)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run - nothing written. Re-run with --yes to rename.");
            return ExitCodes.Success;
        }

        Console.WriteLine();
        int failed = 0;
        var written = new List<DeviceInfo>();
        foreach (var board in boards)
        {
            // Renames run one board at a time: each is a flash-page write followed by a USB drop, and
            // serialising keeps a failure attributable to the board that caused it.
            try
            {
                await BoardRenamer.RenameAsync(board, request.Name, request.Reboot, loggerFactory, ct)
                    .ConfigureAwait(false);
                written.Add(board);
                Console.WriteLine($"  OK     {BoardRename.Describe(board)} - wrote '{request.Name}'");
            }
            catch (OperationCanceledException)
            {
                throw; // Ctrl+C - the shared run loop reports it.
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  FAILED {BoardRename.Describe(board)} - {ex.Message}");
            }
        }

        if (boards.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"Summary: {written.Count} written, {failed} failed.");
        }
        if (written.Count > 0)
            WriteCacheNote(written);

        return failed == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    /// <summary>
    /// Says the part that otherwise costs an afternoon: the write landed, and this host will keep
    /// reporting the old name anyway.
    /// </summary>
    /// <remarks>
    /// Measured on Windows, not inferred. The name a tool reads is
    /// <c>DEVPKEY_Device_FriendlyName</c>, which the hub driver writes from the USB <c>iProduct</c>
    /// string when the device node is first created and never refreshes. The node is keyed by serial,
    /// which a rename deliberately does not change, so it survives the reboot, a port cycle, a PnP
    /// disable/enable and a physical replug alike — every remedy an operator reaches for first. Only
    /// rebuilding the node re-reads <c>iProduct</c>, so print that command with the board's own id
    /// already filled in rather than leaving it as an exercise.
    /// </remarks>
    private static void WriteCacheNote(IReadOnlyList<DeviceInfo> written)
    {
        Console.WriteLine();
        Console.WriteLine("Written to the board's config area. Seeing the new name is a separate matter: a");
        Console.WriteLine("host caches a device's name when it first enumerates it, so this machine will keep");
        Console.WriteLine("reporting the old one - a reboot, a port cycle, even a physical replug all reuse");
        Console.WriteLine("that cached entry. A machine that has never seen the board is unaffected.");

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine();
            Console.WriteLine("To refresh it here, rebuild the device node from an elevated shell:");
            foreach (var b in written)
            {
                Console.WriteLine($"  pnputil /remove-device \"{b.Id}\"");
                Console.WriteLine("  pnputil /scan-devices");
            }
        }
    }

    private static string HelpText(string toolCommand) =>
        $"""
        {toolCommand} {BoardRename.Usage}

        {BoardRename.Summary}
        The name is stored in the board's EEPROM and survives power cycles. It is what shows up as the
        USB product name; the serial number - how 'list' and --target identify a board - is untouched.

        Seeing the new name is a separate matter from writing it, and harder. A host caches a device's
        name when it first enumerates it, keyed by serial - which a rename does not change. So a
        reboot, a USB port cycle, a PnP disable/enable and a physical replug all reuse the cached
        entry and keep showing the old name. On Windows, rebuilding the device node is what re-reads
        it; the command is printed after a write. A machine that has never seen the board is fine.

        {BoardRename.OptionsHelp}
          -v, --verbose         Log the board open + transaction detail to stderr.
          -h, --help            Show this help.

        EXAMPLES
          {toolCommand} rename "Kiosk 3"                 Dry run against the only connected board.
          {toolCommand} rename "Kiosk 3" --yes           Write it.
          {toolCommand} rename Bench-A -t 8mb3de9 -y     Write it to one board by serial.
          {toolCommand} rename Fleet-2026 --all -y       Write it to every connected board.

        EXIT CODES
          0 ok / clean dry run   1 a rename failed   2 usage   4 no matching board
        """;
}
