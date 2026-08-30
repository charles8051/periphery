namespace Periphery.FlashAnything.Cli.Tests;

/// <summary>The hand-rolled CLI parser is pure and total — exercise every shape.</summary>
public class CliParserTests
{
    private static Parsed Parse(params string[] args)
    {
        var r = Cli.Parse(args);
        Assert.Null(r.Error);
        return r.Value!;
    }

    private static string Error(params string[] args)
    {
        var r = Cli.Parse(args);
        Assert.NotNull(r.Error);
        return r.Error!;
    }

    [Fact]
    public void No_args_defaults_to_list() => Assert.Equal(Command.List, Parse().Command);

    [Fact]
    public void Help_variants()
    {
        Assert.Equal(Command.Help, Parse("--help").Command);
        Assert.Equal(Command.Help, Parse("-h").Command);
        Assert.Equal(Command.Help, Parse("help").Command);
    }

    [Fact]
    public void Version_flag() => Assert.Equal(Command.Version, Parse("--version").Command);

    [Fact]
    public void Unknown_command_errors() => Assert.Contains("Unknown command", Error("frobnicate"));

    [Fact]
    public void Flash_requires_file() => Assert.Contains("--file", Error("flash"));

    [Fact]
    public void Autoflash_requires_file() => Assert.Contains("--file", Error("autoflash"));

    [Fact]
    public void Autoflash_parses_file_family_and_yes()
    {
        var p = Parse("autoflash", "--file", "fw.bin", "--family", "STM32 USB DFU", "--yes");
        Assert.Equal(Command.Autoflash, p.Command);
        Assert.Equal("fw.bin", p.File);
        Assert.Equal("STM32 USB DFU", p.Family);
        Assert.True(p.Yes);
    }

    [Fact]
    public void Family_requires_a_value() => Assert.Contains("--family requires", Error("autoflash", "-f", "x", "--family"));

    [Fact]
    public void Flash_parses_file_and_yes()
    {
        var p = Parse("flash", "--file", "fw.bin", "--yes");
        Assert.Equal(Command.Flash, p.Command);
        Assert.Equal("fw.bin", p.File);
        Assert.True(p.Yes);
    }

    [Fact]
    public void Flash_short_flags()
    {
        var p = Parse("flash", "-f", "fw.bin", "-y", "-t", "dev1");
        Assert.Equal("fw.bin", p.File);
        Assert.True(p.Yes);
        Assert.Equal("dev1", p.Target);
    }

    [Fact]
    public void Base_parses_hex_and_decimal()
    {
        Assert.Equal(0x08004000u, Parse("flash", "-f", "x", "--base", "0x08004000").BaseAddress);
        Assert.Equal(4096u, Parse("flash", "-f", "x", "--base", "4096").BaseAddress);
    }

    [Fact]
    public void Base_invalid_errors() => Assert.Contains("Invalid --base", Error("flash", "-f", "x", "--base", "zzz"));

    [Fact]
    public void No_leave_and_no_verify_flags()
    {
        var p = Parse("flash", "-f", "x", "--no-leave", "--no-verify");
        Assert.True(p.NoLeave);
        Assert.True(p.NoVerify);
    }

    [Fact]
    public void All_and_target_are_mutually_exclusive()
        => Assert.Contains("mutually exclusive", Error("flash", "-f", "x", "--all", "-t", "d"));

    [Fact]
    public void Unknown_option_errors() => Assert.Contains("Unknown option", Error("flash", "-f", "x", "--bogus"));

    [Fact]
    public void Missing_option_value_errors() => Assert.Contains("--file requires", Error("flash", "--file"));

    // ── --bootloader-timeout (app-mode reboot wait) ────────────────────────────

    [Fact]
    public void Bootloader_timeout_is_unset_by_default()
    {
        var p = Parse("flash", "-f", "x");
        Assert.Null(p.BootloaderTimeout);
        Assert.Null(p.EntryOptions); // no options object == the orchestrator's own 15s default
    }

    [Fact]
    public void Bootloader_timeout_parses_seconds()
    {
        var p = Parse("flash", "-f", "x", "--bootloader-timeout", "45");
        Assert.Equal(TimeSpan.FromSeconds(45), p.BootloaderTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), p.EntryOptions!.BootloaderTimeout);
    }

    [Fact]
    public void Bootloader_timeout_accepts_fractional_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(2.5), Parse("flash", "-f", "x", "--bootloader-timeout", "2.5").BootloaderTimeout);

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("zzz")]
    [InlineData("999999")] // past the 24h sanity bound
    public void Bootloader_timeout_rejects_nonsense(string value)
        => Assert.Contains("Invalid --bootloader-timeout", Error("flash", "-f", "x", "--bootloader-timeout", value));

    [Fact]
    public void Bootloader_timeout_requires_a_value()
        => Assert.Contains("--bootloader-timeout requires", Error("flash", "-f", "x", "--bootloader-timeout"));

    [Fact]
    public void Bootloader_timeout_is_documented_in_help()
        => Assert.Contains("--bootloader-timeout", Cli.HelpText("flashany", "banner"), StringComparison.Ordinal);

    // ── Front-end-contributed verbs (CliVerb) ──────────────────────────────────
    //
    // The toolkit routes a composition's own verb (e.g. the Treehopper Flasher's `rename`) without
    // parsing it — DEC-006 says a branded flasher composes this CLI rather than forking it.

    private static readonly CliVerb Rename = new(
        "rename", "rename <name> [opts]", "Rename a board.",
        (_, _, _) => Task.FromResult(0))
    {
        OptionsHelp = "RENAME OPTIONS\n      --no-reboot       Skip the reboot.",
    };

    private static readonly CliVerb[] Verbs = [Rename];

    [Fact]
    public void A_contributed_verb_is_routed_with_its_arguments_verbatim()
    {
        var r = Cli.Parse(["rename", "Kiosk 3", "--no-reboot", "-y"], Verbs);
        Assert.Null(r.Error);
        var p = r.Value!;
        Assert.Equal(Command.Verb, p.Command);
        Assert.Same(Rename, p.Verb);
        Assert.Equal(["Kiosk 3", "--no-reboot", "-y"], p.VerbArgs);
    }

    [Fact]
    public void A_contributed_verb_with_no_arguments_gets_an_empty_array()
        => Assert.Empty(Cli.Parse(["rename"], Verbs).Value!.VerbArgs);

    [Fact]
    public void Verbose_is_read_off_a_contributed_verbs_arguments()
    {
        // The log sink is installed process-wide before dispatch, so -v has to be seen here even
        // though everything else in the tail belongs to the verb.
        Assert.True(Cli.Parse(["rename", "x", "--verbose"], Verbs).Value!.Verbose);
        Assert.True(Cli.Parse(["rename", "x", "-v"], Verbs).Value!.Verbose);
        Assert.False(Cli.Parse(["rename", "x"], Verbs).Value!.Verbose);
    }

    [Fact]
    public void An_unknown_verb_still_errors_when_verbs_are_supplied()
        => Assert.Contains("Unknown command", Cli.Parse(["frobnicate"], Verbs).Error!);

    [Fact]
    public void Built_in_verbs_cannot_be_shadowed()
    {
        var shadow = new CliVerb("flash", "flash", "nope", (_, _, _) => Task.FromResult(0));
        Assert.Equal(Command.Flash, Cli.Parse(["flash", "-f", "fw.bin"], [shadow]).Value!.Command);
    }

    [Fact]
    public void Global_help_and_version_win_over_a_contributed_verb()
    {
        Assert.Equal(Command.Help, Cli.Parse(["--help"], Verbs).Value!.Command);
        Assert.Equal(Command.Version, Cli.Parse(["--version"], Verbs).Value!.Command);
    }

    [Fact]
    public void A_contributed_verb_owns_its_own_help_flag()
    {
        // `rename --help` must reach the verb, not print the tool's help over the top of it.
        var p = Cli.Parse(["rename", "--help"], Verbs).Value!;
        Assert.Equal(Command.Verb, p.Command);
        Assert.Equal(["--help"], p.VerbArgs);
    }

    [Fact]
    public void A_contributed_verb_is_documented_in_help()
    {
        string help = Cli.HelpText("treehopper-flash", "banner", Verbs);
        Assert.Contains("treehopper-flash rename <name> [opts]", help, StringComparison.Ordinal);
        Assert.Contains("Rename a board.", help, StringComparison.Ordinal);
        Assert.Contains("RENAME OPTIONS", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_is_unchanged_when_a_front_end_contributes_nothing()
    {
        Assert.Equal(Cli.HelpText("flashany", "banner"), Cli.HelpText("flashany", "banner", []));
        Assert.DoesNotContain("RENAME", Cli.HelpText("flashany", "banner"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_global_flag_before_the_verb_names_the_verb_in_the_error()
    {
        // A leading flag routes to the default `list` command - the pre-existing rule for the
        // built-ins. Rather than then blaming the verb token as an unknown list option, the parser
        // recognises the shape and says what the user should have typed.
        string err = Cli.Parse(["--verbose", "rename", "x"], Verbs).Error!;
        Assert.Contains("Global flags must come after the command", err);
        Assert.Contains("rename", err);

        // Same for a built-in verb, with no contributed verbs in play at all.
        Assert.Contains("Global flags must come after the command", Cli.Parse(["-v", "flash", "-f", "x"]).Error!);
    }

    [Fact]
    public void The_seam_owns_verbose_and_strips_it_from_the_verbs_arguments()
    {
        // One owner: the run loop reads -v to install the log sink AND removes it, so a verb never
        // sees it and cannot disagree with the run loop about whether verbosity was asked for.
        var p = Cli.Parse(["rename", "-v", "x"], Verbs).Value!;
        Assert.True(p.Verbose);
        Assert.Equal(["x"], p.VerbArgs);

        var q = Cli.Parse(["rename", "x", "--verbose", "-y"], Verbs).Value!;
        Assert.True(q.Verbose);
        Assert.Equal(["x", "-y"], q.VerbArgs);
    }

    [Fact]
    public void A_verb_without_verbose_keeps_its_arguments_untouched()
    {
        var p = Cli.Parse(["rename", "x", "-y"], Verbs).Value!;
        Assert.False(p.Verbose);
        Assert.Equal(["x", "-y"], p.VerbArgs);
    }
}
