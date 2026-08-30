// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.FlashAnything.Cli;

namespace Periphery.Treehopper.Flasher.Cli;

/// <summary>
/// The <c>--target</c> / <c>--all</c> / <c>--yes</c> targeting shared by the board-level verbs
/// (<c>reboot</c>, <c>rescue</c>): parsing, the mutual-exclusion rules, the confirmation gate, and
/// resolving the selection against the boards actually present.
/// </summary>
/// <remarks>
/// Extracted rather than copied. Both verbs perform an irreversible, board-disrupting reset, so
/// they must agree exactly on when <c>--all</c> is allowed to fan out unattended — two hand-kept
/// copies of a confirmation gate is how one of them ends up missing it.
/// </remarks>
internal static class VerbTargeting
{
    /// <summary>
    /// The parsed selection. <see cref="ExitCode"/> is non-null when parsing has already produced
    /// the verb's answer (a usage error, or <c>--help</c>) and the caller should return it.
    /// </summary>
    internal readonly record struct Selection(string? Target, bool All, int? ExitCode)
    {
        public static Selection Exit(int code) => new(null, false, code);
    }

    /// <summary>
    /// Parses the shared targeting options. <paramref name="allWarning"/> is the one line that
    /// describes what <c>--all</c> would do to every board, shown when it is used without
    /// <c>--yes</c>.
    /// </summary>
    public static Selection Parse(string[] args, string verb, string allWarning, Action writeHelp)
    {
        string? target = null;
        bool all = false, yes = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            // Accept both `--target <id>` and `--target=<id>`; the latter is the convention the
            // rest of the CLI surface and most dotnet tooling accept.
            string? inlineValue = null;
            int eq = a.IndexOf('=');
            if (eq > 0) { inlineValue = a[(eq + 1)..]; a = a[..eq]; }

            switch (a)
            {
                case "--target" or "-t":
                    if (target is not null)
                    {
                        Console.Error.WriteLine("--target given more than once; pass it once, or use --all.");
                        return Selection.Exit(ExitCodes.Usage);
                    }
                    if (inlineValue is not null) target = inlineValue;
                    else
                    {
                        if (++i >= args.Length) { Console.Error.WriteLine("--target requires a device id."); return Selection.Exit(ExitCodes.Usage); }
                        target = args[i];
                    }
                    if (string.IsNullOrWhiteSpace(target)) { Console.Error.WriteLine("--target requires a non-empty device id."); return Selection.Exit(ExitCodes.Usage); }
                    break;
                case "--all":
                    all = true;
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                // No -v arm: the seam owns --verbose, installing the sink and stripping the flag
                // before dispatch, so it never reaches here (CliVerb).
                case "-h" or "--help":
                    writeHelp();
                    return Selection.Exit(ExitCodes.Success);
                default:
                    Console.Error.WriteLine($"unknown option '{args[i]}' for {verb}.");
                    return Selection.Exit(ExitCodes.Usage);
            }
        }

        if (all && target is not null)
        {
            Console.Error.WriteLine("--all and --target are mutually exclusive; pass one.");
            return Selection.Exit(ExitCodes.Usage);
        }
        // --yes only gates --all. Accepting it silently on a single-target run would imply a
        // confirmation is needed for every reset, which it is not.
        if (yes && !all)
        {
            Console.Error.WriteLine($"--yes is only meaningful with --all; a single --target {verb} needs no confirmation.");
            return Selection.Exit(ExitCodes.Usage);
        }
        // A reset drops the USB link out from under whatever is driving the board. Fanning that
        // across every board unattended is not something to do on a bare flag, so --all is gated
        // behind an explicit confirmation.
        if (all && !yes)
        {
            Console.Error.WriteLine(allWarning);
            Console.Error.WriteLine("Re-run with --yes to confirm, or target a single board with --target <id>.");
            return Selection.Exit(ExitCodes.Usage);
        }

        return new Selection(target, all, ExitCode: null);
    }

    /// <summary>
    /// Resolves <paramref name="parsed"/> against the boards present. Returns <c>null</c> — with
    /// <paramref name="exitCode"/> set and the reason already printed — when the selection cannot
    /// be honoured.
    /// </summary>
    public static IReadOnlyList<DeviceInfo>? Select(
        IReadOnlyList<DeviceInfo> boards, Selection parsed, string verb, out int exitCode)
    {
        // Id comparison is DeviceId's, i.e. case-insensitive: a board re-enumerates with different
        // casing in its instance id and is still the same board (#231).
        var chosen = boards.Where(b => parsed.All || parsed.Target is null || b.Id == parsed.Target).ToList();

        if (parsed.Target is not null && chosen.Count == 0)
        {
            Console.Error.WriteLine($"No board matched --target '{parsed.Target}'. Present:");
            foreach (var b in boards) Console.Error.WriteLine($"  {b.Id}  {b.Name}");
            exitCode = ExitCodes.NoTarget;
            return null;
        }

        if (!parsed.All && parsed.Target is null && boards.Count > 1)
        {
            Console.Error.WriteLine("Multiple boards present - pass --target <id> or --all --yes. Present:");
            foreach (var b in boards) Console.Error.WriteLine($"  {b.Id}  {b.Name}");
            exitCode = ExitCodes.Usage;
            return null;
        }

        exitCode = ExitCodes.Success;
        return chosen;
    }
}
