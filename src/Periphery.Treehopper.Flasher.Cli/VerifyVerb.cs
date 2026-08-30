// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.FlashAnything.Cli;
using Periphery.Treehopper;
using Periphery.Treehopper.Firmware;

namespace Periphery.Treehopper.Flasher.Cli;

/// <summary>
/// The <c>verify</c> verb: check whether a board's <b>current</b> flash content matches a given
/// image, without reflashing it (<see cref="TreehopperFirmwareUpdate.VerifyFromFileAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, not just "flash and trust the OK."</b> A flash's own embedded Verify record
/// is not independent proof its content landed — <c>Efm8HidProgrammer.FlashAsync</c> marks its
/// result <c>verified: false</c> for exactly that reason (periphery#246). This verb runs a Verify
/// check in a genuinely separate, later bootloader session: reboot into the bootloader, ask "does
/// what's already there match this image," reboot back out — no Erase or Write record exists in the
/// stream it sends, so it cannot modify firmware no matter the answer.
/// </para>
/// <para>
/// <b>This still drops the USB link, like <c>reboot</c> and <c>rescue</c>.</b> Checking requires
/// entering the bootloader, which any application driving the board will lose regardless of the
/// read-only outcome. Do not run against a live workload.
/// </para>
/// </remarks>
internal static class VerifyVerb
{
    /// <summary>The verb, as the shared CLI routes and documents it.</summary>
    public static CliVerb Create() =>
        new("verify", "verify --file <hex> (--target <id> | --all)",
            "Check whether a board's current flash content matches an image, without reflashing it.",
            RunAsync)
        {
            OptionsHelp =
                """
                VERIFY OPTIONS
                  -f, --file <path>     An Intel HEX (.hex) image to compare against. Required.
                  -t, --target <id>     Verify one board (also accepts --target=<id>).
                      --all             Verify every detected board.

                  Reboots into the bootloader and asks whether the board's CURRENT flash content
                  matches --file, then reboots back out. No Erase or Write record exists in the
                  check it sends, so this cannot modify firmware regardless of the answer - but it
                  still drops the USB link like reboot/rescue do. Do not run against a live workload.
                """,
        };

    private static async Task<int> RunAsync(string[] args, ILoggerFactory? loggerFactory, CancellationToken ct)
    {
        // --file isn't part of VerbTargeting's shared --target/--all/--yes surface (reboot/rescue
        // have nothing to name), so it is pulled out of args here, before the rest is handed to the
        // shared parser - which would otherwise reject it as an unknown option.
        var (filePath, remaining, fileError) = ExtractFileOption(args);
        if (fileError is { } usage)
        {
            Console.Error.WriteLine(usage);
            HelpText();
            return ExitCodes.Usage;
        }
        if (string.IsNullOrEmpty(filePath))
        {
            Console.Error.WriteLine("verify requires --file <path.hex>.");
            HelpText();
            return ExitCodes.Usage;
        }

        var parsed = VerbTargeting.Parse(
            remaining, "verify",
            "--all verifies EVERY detected board, dropping each USB link while it checks.",
            HelpText);
        if (parsed.ExitCode is { } usageExit) return usageExit;

        var boards = await TreehopperBoard.EnumerateAsync(ct);
        if (boards.Count == 0) { Console.Error.WriteLine("No Treehopper boards found."); return ExitCodes.NoTarget; }

        var chosen = VerbTargeting.Select(boards, parsed, "verify", out int selectionExit);
        if (chosen is null) return selectionExit;

        int mismatched = 0;
        foreach (var info in chosen)
        {
            Console.WriteLine($"Verifying {info.Name} ({info.Id}) against {filePath} ...");
            try
            {
                var result = await TreehopperFirmwareUpdate.VerifyFromFileAsync(info, filePath, ct: ct);
                if (result.Matches)
                {
                    Console.WriteLine("  MATCH - the board's current flash content matches the image.");
                }
                else if (result.ContentMismatch)
                {
                    Console.Error.WriteLine(
                        "  MISMATCH - the board's current flash content does NOT match the image "
                        + "(the bootloader's own Verify record was rejected).");
                    mismatched++;
                }
                else
                {
                    Console.Error.WriteLine($"  INCONCLUSIVE - {result.Upload.Describe()}");
                    mismatched++;
                }

                // A MATCH the board never confirmed leaving the bootloader for is not something to
                // report success on - the content check and the board's actual state are two
                // different claims, and only reporting the first would let exit code 0 mean "still
                // stuck in the bootloader" as long as the content happened to check out.
                if (!result.ApplicationReturned)
                {
                    Console.Error.WriteLine(
                        "  WARNING - the application did not re-enumerate after the check; "
                        + "confirm the board came back before trusting anything else about it.");
                    mismatched++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  FAILED - {ex.GetBaseException().Message}");
                mismatched++;
            }
        }

        return mismatched == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    // Pulls --file/-f (and its --file=<path> form, matching the rest of the CLI's convention) out
    // of args, returning what's left for VerbTargeting.Parse. A usage error here is returned as a
    // message, not thrown, so the caller can print it the same way VerbTargeting's own errors are.
    private static (string? File, string[] Remaining, string? Error) ExtractFileOption(string[] args)
    {
        string? file = null;
        var remaining = new List<string>(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? inlineValue = null;
            int eq = a.IndexOf('=');
            string flag = a;
            if (eq > 0 && (a.StartsWith("--file", StringComparison.Ordinal) || a.StartsWith("-f", StringComparison.Ordinal)))
            {
                inlineValue = a[(eq + 1)..];
                flag = a[..eq];
            }

            if (flag is "--file" or "-f")
            {
                if (file is not null)
                    return (null, [], "--file given more than once; pass it once.");
                if (inlineValue is not null) { file = inlineValue; }
                else
                {
                    if (++i >= args.Length)
                        return (null, [], "--file requires a path.");
                    file = args[i];
                }
                if (string.IsNullOrWhiteSpace(file))
                    return (null, [], "--file requires a non-empty path.");
            }
            else
            {
                remaining.Add(a);
            }
        }

        return (file, remaining.ToArray(), null);
    }

    private static void HelpText()
    {
        Console.WriteLine("treehopper-flash verify --file <hex> (--target <id> | --all)");
        Console.WriteLine("  Check whether a board's CURRENT flash content matches an image, without");
        Console.WriteLine("  reflashing it. Independent of any flash's own embedded verify (periphery#246)");
        Console.WriteLine("  - a genuinely separate, later bootloader session, not a re-read of the same one.");
        Console.WriteLine();
        Console.WriteLine("  -f, --file <path>  An Intel HEX (.hex) image to compare against. Required.");
        Console.WriteLine("  -t, --target <id>  Verify one board (also accepts --target=<id>).");
        Console.WriteLine("      --all          Verify every detected board.");
        Console.WriteLine("  -v, --verbose      Log the USB open + transfer detail to stderr.");
        Console.WriteLine();
        Console.WriteLine("  No Erase or Write record exists in the check this sends, so it cannot modify");
        Console.WriteLine("  firmware regardless of the answer - but it still drops the USB link like");
        Console.WriteLine("  reboot/rescue do. Do not run against a live workload.");
        Console.WriteLine();
        Console.WriteLine("  Exit: 0 match (and the app confirmed leaving the bootloader)  1 mismatch,");
        Console.WriteLine("        inconclusive, or the app didn't come back  2 usage  4 no matching board");
    }
}
