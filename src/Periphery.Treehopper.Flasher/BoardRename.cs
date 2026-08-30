// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;

namespace Periphery.Treehopper.Flasher;

/// <summary>A validated <c>rename</c> command line. Produced only by <see cref="BoardRename.Parse"/>.</summary>
public sealed record BoardRenameRequest
{
    /// <summary>The new device name to write. Already validated by <see cref="BoardRename.ValidateName"/>.</summary>
    public required string Name { get; init; }

    /// <summary>The board to rename, by serial number or device id; <c>null</c> means "the only one".</summary>
    public string? Target { get; init; }

    /// <summary>Rename every connected board (mutually exclusive with <see cref="Target"/>).</summary>
    public bool All { get; init; }

    /// <summary><c>--yes</c>. Without it the command is a dry run and writes nothing.</summary>
    public bool Apply { get; init; }

    /// <summary>
    /// Reboot each board after writing. Cleared by <c>--no-reboot</c>. Best-effort and never
    /// load-bearing: the reboot opcode does re-enumerate the board, but on Windows even a successful
    /// re-enumeration does not refresh the host's cached name (see <see cref="BoardRenamer"/>).
    /// </summary>
    public bool Reboot { get; init; } = true;
}

/// <summary>The outcome of parsing a <c>rename</c> line: exactly one of the three is meaningful.</summary>
public readonly record struct BoardRenameParse(BoardRenameRequest? Value, string? Error, bool HelpRequested);

/// <summary>The boards a request resolves to, or the reason it resolves to none.</summary>
public readonly record struct BoardRenameSelection(IReadOnlyList<DeviceInfo> Boards, string? Error);

/// <summary>
/// The pure core of the Treehopper Flasher's <c>rename</c> verb (ADR-0052): argument parsing, name
/// validation, and board selection are total functions over values — no USB, no clock, no console.
/// <see cref="BoardRenamer"/> is the thin shell that opens a board and writes.
/// </summary>
public static class BoardRename
{
    /// <summary>The verb, as typed.</summary>
    public const string Verb = "rename";

    /// <summary>
    /// The longest name the board accepts. The firmware stores the name as a USB string descriptor in
    /// one flash page and <c>TreehopperBoard.UpdateNameAsync</c> rejects anything longer.
    /// </summary>
    public const int MaxNameLength = 60;

    /// <summary>The usage line spliced into the tool's <c>--help</c>.</summary>
    public const string Usage = "rename <name> [opts]";

    /// <summary>The one-line summary spliced into the tool's <c>--help</c>.</summary>
    public const string Summary = "Write a new device name to a connected Treehopper board's EEPROM.";

    /// <summary>The <c>--help</c> block for this verb's own options.</summary>
    public const string OptionsHelp =
        """
        RENAME OPTIONS
          -t, --target <sel>    Board to rename, by serial number or device id as shown by 'list'
                                (default: the only connected board; else --all).
              --all             Rename every connected Treehopper board.
              --no-reboot       Skip the reboot after writing. The name is stored either way; the
                                reboot does not make this host show it (see the note after a write).
          -y, --yes             Actually write the name. Without it, rename is a DRY RUN.
        """;

    /// <summary>
    /// Parses the arguments that followed <c>rename</c>. Pure and total: every input yields a request,
    /// an error, or a help request.
    /// </summary>
    public static BoardRenameParse Parse(string[] args)
    {
        string? name = null, target = null;
        bool all = false, apply = false, reboot = true;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return new BoardRenameParse(null, null, HelpRequested: true);
                case "-t" or "--target":
                    if (++i >= args.Length) return Err("--target requires a serial number or device id.");
                    target = args[i];
                    break;
                case "--all": all = true; break;
                case "-y" or "--yes": apply = true; break;
                case "--no-reboot": reboot = false; break;
                // The CLI seam owns --verbose: it installs the log sink and strips the flag before
                // dispatch, so this never fires from the CLI. Accepted anyway because this parser is
                // public and total - a direct caller passing -v should not get "unknown option".
                case "-v" or "--verbose": break;
                default:
                    if (a.StartsWith('-')) return Err($"Unknown option '{a}'.");
                    if (name is not null)
                        return Err($"rename takes one name; got '{name}' and '{a}'. Quote a name that contains spaces.");
                    name = a;
                    break;
            }
        }

        if (name is null)
            return Err("rename requires a new board name, e.g. 'rename \"Kiosk 3\"'.");
        if (ValidateName(name) is { } invalid)
            return Err(invalid);
        if (all && target is not null)
            return Err("--all and --target are mutually exclusive.");

        return new BoardRenameParse(
            new BoardRenameRequest { Name = name, Target = target, All = all, Apply = apply, Reboot = reboot },
            null, false);

        static BoardRenameParse Err(string message) => new(null, message, false);
    }

    /// <summary>
    /// Checks a proposed device name against what the board can actually store, returning the reason
    /// it cannot be, or <c>null</c> when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why ASCII only</b> — the two ends disagree about what a "length" is for anything else.
    /// <c>TreehopperWire</c> sends the name's <em>UTF-8 bytes</em> under a length byte holding its
    /// <em>character</em> count (<c>packet[1] = (byte)text.Length</c>). The firmware then stores that
    /// many <em>bytes</em>, one per slot, in a descriptor flagged
    /// <c>USB_STRING_DESCRIPTOR_UTF16LE_PACKED</c> — "packed" meaning exactly that it holds one byte
    /// per character and the EFM8 USB stack widens each to a UTF-16LE code unit when the host reads it.
    /// </para>
    /// <para>
    /// For ASCII, one character is one UTF-8 byte and the two counts agree. For anything else they
    /// diverge — the firmware stores a truncated prefix of the UTF-8 bytes and reads it back as
    /// mojibake. So the restriction is a real wire constraint, not caution, and it is enforced here
    /// rather than half-written to flash.
    /// </para>
    /// </remarks>
    public static string? ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
            return "A board name cannot be empty.";
        // All-whitespace passes a naive length check and writes an invisible name; and a stray
        // trailing space is a classic "why doesn't the name match?" report from the field.
        if (string.IsNullOrWhiteSpace(name))
            return "A board name cannot be only whitespace.";
        if (name != name.Trim())
            return "A board name cannot start or end with whitespace; it would be written verbatim "
                 + "and then be invisible in every listing.";
        if (name.Length > MaxNameLength)
            return $"A board name must be {MaxNameLength} characters or fewer (got {name.Length}).";

        foreach (char c in name)
        {
            if (c is < ' ' or > '~')
                return $"A board name must be printable ASCII; U+{(int)c:X4} is not. The wire codec "
                     + "sends UTF-8 bytes under a character-count length byte, and the firmware stores "
                     + "one byte per character - so a wider character is written truncated and read "
                     + "back mangled. Lifting this needs a codec and firmware change, not a longer name.";
        }

        return null;
    }

    /// <summary>
    /// Resolves the boards a request names out of the connected ones. Pure: the caller supplies the
    /// snapshot. An empty <see cref="BoardRenameSelection.Boards"/> always comes with an
    /// <see cref="BoardRenameSelection.Error"/> explaining why.
    /// </summary>
    public static BoardRenameSelection Select(IReadOnlyList<DeviceInfo> boards, BoardRenameRequest request)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(request);

        if (boards.Count == 0)
            return new BoardRenameSelection([], "No Treehopper board is connected.");

        if (request.Target is { } selector)
        {
            var matched = boards.Where(b => Matches(b, selector)).ToList();
            if (matched.Count == 0)
                return new BoardRenameSelection([], $"No connected Treehopper board matches '{selector}'.{Listing(boards)}");
            // A selector names one board. If it somehow names several — a serial that is also another
            // board's device id — refuse rather than quietly renaming every one of them.
            if (matched.Count > 1)
                return new BoardRenameSelection(
                    [], $"'{selector}' matches {matched.Count} connected boards; use a device id.{Listing(matched)}");
            return new BoardRenameSelection(matched, null);
        }

        if (request.All)
            return new BoardRenameSelection(boards, null);

        if (boards.Count == 1)
            return new BoardRenameSelection([boards[0]], null);

        return new BoardRenameSelection(
            [],
            $"{boards.Count} Treehopper boards are connected; pass --target <serial|id> or --all.{Listing(boards)}");
    }

    /// <summary>True if <paramref name="selector"/> names this board by serial number or device id.</summary>
    private static bool Matches(DeviceInfo board, string selector) =>
        string.Equals(board.SerialNumber, selector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(board.Id.ToString(), selector, StringComparison.OrdinalIgnoreCase);

    /// <summary>The "here is what is connected" tail appended to a selection error.</summary>
    private static string Listing(IReadOnlyList<DeviceInfo> boards)
    {
        var sb = new StringBuilder();
        foreach (var b in boards)
            sb.Append('\n').Append("  ").Append(Describe(b));
        return sb.ToString();
    }

    /// <summary>The one-line rendering of a board: how it is selected, then what it currently calls itself.</summary>
    public static string Describe(DeviceInfo board)
    {
        ArgumentNullException.ThrowIfNull(board);
        string id = board.SerialNumber ?? board.Id.ToString();
        return board.Name is { } name ? $"{id}  {name}" : id;
    }
}
