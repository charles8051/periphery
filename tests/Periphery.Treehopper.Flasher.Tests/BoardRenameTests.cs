using Periphery;

namespace Periphery.Treehopper.Flasher.Tests;

/// <summary>
/// The <c>rename</c> verb's pure core: parsing, name validation, and board selection are total
/// functions over values, so every shape is exercised here with no hardware.
/// </summary>
public class BoardRenameTests
{
    private static BoardRenameRequest Parse(params string[] args)
    {
        var r = BoardRename.Parse(args);
        Assert.Null(r.Error);
        Assert.False(r.HelpRequested);
        return r.Value!;
    }

    private static string Error(params string[] args)
    {
        var r = BoardRename.Parse(args);
        Assert.NotNull(r.Error);
        Assert.Null(r.Value);
        return r.Error!;
    }

    private static DeviceInfo Board(string serial, string? name = "Treehopper") => new()
    {
        Id = $"USB\\VID_10C4&PID_8A7E\\{serial}",
        Name = name,
        SerialNumber = serial,
    };

    // ── Parsing ────────────────────────────────────────────────────────────────

    [Fact]
    public void Name_is_positional_and_everything_else_defaults()
    {
        var p = Parse("Kiosk 3");
        Assert.Equal("Kiosk 3", p.Name);
        Assert.Null(p.Target);
        Assert.False(p.All);
        Assert.False(p.Apply);      // dry run by default, like flash
        Assert.True(p.Reboot);      // best-effort; never load-bearing for the write
    }

    [Fact]
    public void Flags_parse()
    {
        var p = Parse("Bench-A", "--target", "8mb3de9", "--yes", "--no-reboot");
        Assert.Equal("Bench-A", p.Name);
        Assert.Equal("8mb3de9", p.Target);
        Assert.True(p.Apply);
        Assert.False(p.Reboot);
    }

    [Fact]
    public void Short_flags_parse()
    {
        var p = Parse("Bench-A", "-t", "8mb3de9", "-y");
        Assert.Equal("8mb3de9", p.Target);
        Assert.True(p.Apply);
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("--verbose")]
    public void Verbose_is_accepted_and_ignored(string flag)
    {
        // The shared run loop reads -v off the tail to install the log sink but leaves it in the
        // arguments, so the verb must tolerate it rather than reject it as unknown (CliVerb contract).
        Assert.Equal("Bench-A", Parse("Bench-A", flag).Name);
    }

    [Fact]
    public void Name_may_follow_the_flags()
    {
        var p = Parse("--all", "-y", "Fleet-2026");
        Assert.Equal("Fleet-2026", p.Name);
        Assert.True(p.All);
    }

    [Fact]
    public void Help_is_requested_not_an_error()
    {
        var r = BoardRename.Parse(["-h"]);
        Assert.True(r.HelpRequested);
        Assert.Null(r.Error);
        Assert.Null(r.Value);
    }

    [Fact]
    public void Help_wins_over_a_bad_line()
        => Assert.True(BoardRename.Parse(["--help", "--bogus"]).HelpRequested);

    [Fact]
    public void Name_is_required() => Assert.Contains("requires a new board name", Error());

    [Fact]
    public void Only_one_name_is_accepted() => Assert.Contains("takes one name", Error("Kiosk", "3"));

    [Fact]
    public void Target_requires_a_value() => Assert.Contains("--target requires", Error("x", "--target"));

    [Fact]
    public void Unknown_option_errors() => Assert.Contains("Unknown option '--bogus'", Error("x", "--bogus"));

    [Fact]
    public void All_and_target_are_mutually_exclusive()
        => Assert.Contains("mutually exclusive", Error("x", "--all", "-t", "y"));

    [Fact]
    public void Parse_rejects_an_unwritable_name()
        => Assert.Contains("printable ASCII", Error("Kioské"));

    // ── Name validation (what the board can actually store) ────────────────────

    [Fact]
    public void A_plain_name_is_valid() => Assert.Null(BoardRename.ValidateName("Kiosk 3"));

    [Fact]
    public void Empty_is_rejected() => Assert.Contains("cannot be empty", BoardRename.ValidateName("")!);

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Whitespace_only_is_rejected(string name)
        => Assert.Contains("only whitespace", BoardRename.ValidateName(name)!);

    [Theory]
    [InlineData("Kiosk 3 ")]   // the classic "why doesn't the name match?" report
    [InlineData(" Kiosk 3")]
    public void Surrounding_whitespace_is_rejected(string name)
        => Assert.Contains("start or end with whitespace", BoardRename.ValidateName(name)!);

    [Fact]
    public void Interior_spaces_are_fine() => Assert.Null(BoardRename.ValidateName("Deposit Chamber 2"));

    [Fact]
    public void Sixty_characters_is_the_limit()
    {
        Assert.Null(BoardRename.ValidateName(new string('a', BoardRename.MaxNameLength)));
        Assert.Contains("60 characters or fewer", BoardRename.ValidateName(new string('a', 61))!);
    }

    [Theory]
    [InlineData("Kioské")]   // Latin-1: two UTF-8 bytes under a one-char length byte
    [InlineData("Kiosk中")]   // beyond one byte entirely
    [InlineData("Kiosk\n3")]      // control characters are not printable descriptor content
    [InlineData("Kiosk\t3")]
    public void Non_printable_ascii_is_rejected(string name)
        => Assert.Contains("printable ASCII", BoardRename.ValidateName(name)!);

    [Fact]
    public void The_printable_ascii_range_is_accepted()
    {
        // Every char in 0x20..0x7E is legal *within* a name. Sliced to stay under the length cap and
        // bookended with 'x' so the space at 0x20 is never leading or trailing (which is its own rule).
        const int slice = BoardRename.MaxNameLength - 2;
        for (int start = ' '; start <= '~'; start += slice)
        {
            int end = Math.Min(start + slice - 1, '~');
            var chunk = new string([.. Enumerable.Range(start, end - start + 1).Select(c => (char)c)]);
            Assert.Null(BoardRename.ValidateName($"x{chunk}x"));
        }
    }

    // ── Selection ──────────────────────────────────────────────────────────────

    private static readonly BoardRenameRequest Plain = new() { Name = "X" };

    [Fact]
    public void Nothing_connected_is_an_error()
    {
        var s = BoardRename.Select([], Plain);
        Assert.Empty(s.Boards);
        Assert.Contains("No Treehopper board is connected", s.Error!);
    }

    [Fact]
    public void One_connected_board_is_the_default_target()
    {
        var only = Board("CDYHINBH");
        var s = BoardRename.Select([only], Plain);
        Assert.Null(s.Error);
        Assert.Equal([only], s.Boards);
    }

    [Fact]
    public void Several_boards_need_target_or_all()
    {
        var s = BoardRename.Select([Board("aaa"), Board("bbb")], Plain);
        Assert.Empty(s.Boards);
        Assert.Contains("pass --target <serial|id> or --all", s.Error!);
        Assert.Contains("aaa", s.Error!);   // and says which ones are there
        Assert.Contains("bbb", s.Error!);
    }

    [Fact]
    public void All_selects_every_board()
    {
        var boards = new[] { Board("aaa"), Board("bbb") };
        var s = BoardRename.Select(boards, Plain with { All = true });
        Assert.Null(s.Error);
        Assert.Equal(boards, s.Boards);
    }

    [Fact]
    public void Target_selects_by_serial_case_insensitively()
    {
        var wanted = Board("8mb3de9");
        var s = BoardRename.Select([Board("aaa"), wanted], Plain with { Target = "8MB3DE9" });
        Assert.Null(s.Error);
        Assert.Equal([wanted], s.Boards);
    }

    [Fact]
    public void Target_selects_by_device_id()
    {
        var wanted = Board("8mb3de9");
        var s = BoardRename.Select([Board("aaa"), wanted], Plain with { Target = wanted.Id.ToString() });
        Assert.Null(s.Error);
        Assert.Equal([wanted], s.Boards);
    }

    [Fact]
    public void An_ambiguous_target_is_refused_rather_than_renaming_both()
    {
        // Contrived but cheap to be safe about: one board's serial is another's device id. Renaming
        // both because the selector was ambiguous is the wrong answer for a --target that means "one".
        var a = new DeviceInfo { Id = "shared-token", Name = "A", SerialNumber = "aaa" };
        var b = new DeviceInfo { Id = "USB\\x\\bbb", Name = "B", SerialNumber = "shared-token" };
        var s = BoardRename.Select([a, b], Plain with { Target = "shared-token" });
        Assert.Empty(s.Boards);
        Assert.Contains("matches 2 connected boards", s.Error!);
    }

    [Fact]
    public void An_unmatched_target_lists_what_is_connected()
    {
        var s = BoardRename.Select([Board("aaa")], Plain with { Target = "nope" });
        Assert.Empty(s.Boards);
        Assert.Contains("matches 'nope'", s.Error!);
        Assert.Contains("aaa", s.Error!);
    }

    [Fact]
    public void Describe_falls_back_to_the_device_id_when_there_is_no_serial()
    {
        var noSerial = new DeviceInfo { Id = "USB\\VID_10C4&PID_8A7E\\6&1", Name = null };
        Assert.Equal("USB\\VID_10C4&PID_8A7E\\6&1", BoardRename.Describe(noSerial));
        Assert.Equal("aaa  Treehopper", BoardRename.Describe(Board("aaa")));
    }
}
