// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Periphery;
using Periphery.Bootloader;
using Periphery.Diagnostics;
using Periphery.FlashAnything;

namespace Periphery.FlashAnything.Cli;

internal enum Command { List, Flash, Autoflash, Verb, Help, Version }

/// <summary>A fully-validated command line. Produced only by <see cref="Cli.Parse"/>.</summary>
internal sealed record Parsed
{
    public Command Command { get; init; }
    public string? File { get; init; }          // --file / -f
    public string? Target { get; init; }        // --target / -t (device id)
    public bool All { get; init; }              // --all
    public bool Yes { get; init; }              // --yes / -y (else dry run)
    public uint? BaseAddress { get; init; }     // --base / -b
    public bool NoLeave { get; init; }          // --no-leave
    public bool NoVerify { get; init; }         // --no-verify
    public string? Family { get; init; }        // --family (autoflash provider/family)
    public bool Verbose { get; init; }          // --verbose / -v (console logging to stderr)
    public TimeSpan? BootloaderTimeout { get; init; } // --bootloader-timeout <seconds>; null = the orchestrator's default

    /// <summary>The front-end-contributed verb this line names, for <see cref="Command.Verb"/>.</summary>
    public CliVerb? Verb { get; init; }

    /// <summary>Everything after that verb, passed through verbatim for it to parse itself.</summary>
    public string[] VerbArgs { get; init; } = [];

    /// <summary>
    /// The bootloader-entry tunables this command line asks for, or <c>null</c> when it asks for
    /// none (leaving <see cref="BootloaderEntryOptions"/>'s defaults, notably its 15s timeout).
    /// </summary>
    public BootloaderEntryOptions? EntryOptions =>
        BootloaderTimeout is { } timeout ? new BootloaderEntryOptions { BootloaderTimeout = timeout } : null;
}

internal readonly record struct ParseResult(Parsed? Value, string? Error);

/// <summary>
/// The reusable FlashAnything CLI: a hand-rolled, pure, total argument parser plus the
/// parse-then-dispatch run loop. <see cref="RunAsync"/> takes the composition (a service factory)
/// and branding, so the generic <c>flashany</c> tool and a branded device-specific flasher (DEC-006)
/// are both thin front-ends over it.
/// </summary>
public static class Cli
{
    /// <summary>
    /// Parses argv, prints help/version, wires logging, and dispatches to the list/flash/autoflash
    /// command against a service built by <paramref name="serviceFactory"/> (the curated composition).
    /// </summary>
    /// <param name="serviceFactory">
    /// Builds the <see cref="FlashAnythingService"/> (its registry / entries / converters) over the
    /// log sink and the bootloader-entry tunables this command line asked for (<c>null</c> = defaults).
    /// </param>
    /// <param name="toolName">The command name, used in usage and the logger category (e.g. <c>flashany</c>).</param>
    /// <param name="banner">The one-line product description shown at the top of <c>--help</c>.</param>
    /// <param name="args">The process arguments.</param>
    /// <param name="verbs">
    /// Composition-specific verbs this front-end adds on top of list/flash/autoflash (DEC-006) — e.g.
    /// the Treehopper Flasher's <c>rename</c>. They are routed and documented, not parsed, here.
    /// </param>
    public static async Task<int> RunAsync(
        Func<ILogger?, BootloaderEntryOptions?, FlashAnythingService> serviceFactory,
        string toolName, string banner, string[] args, IReadOnlyList<CliVerb>? verbs = null)
    {
        var parse = Parse(args, verbs);
        if (parse.Error is not null)
        {
            Console.Error.WriteLine(parse.Error);
            Console.Error.WriteLine($"Run '{toolName} --help' for usage.");
            return ExitCodes.Usage;
        }

        var p = parse.Value!;
        switch (p.Command)
        {
            case Command.Help:
                Console.WriteLine(HelpText(toolName, banner, verbs));
                return ExitCodes.Success;
            case Command.Version:
                Console.WriteLine(InformationalVersion());
                return ExitCodes.Success;
        }

        // --verbose wires a stderr console sink. Set the Periphery logger factory BEFORE the service
        // touches any device type (its watcher/providers capture a static logger at type-init).
        ILoggerFactory? loggerFactory = null;
        ILogger? logger = null;
        if (p.Verbose)
        {
            loggerFactory = new SinkLoggerFactory(new ConsoleLogSink(), LogLevel.Debug);
            PeripheryLoggerFactory.SetLoggerFactory(loggerFactory);
            logger = loggerFactory.CreateLogger(toolName);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return p.Command switch
            {
                Command.List => await Commands.ListAsync(serviceFactory, p, logger, cts.Token),
                Command.Flash => await Commands.FlashAsync(serviceFactory, p, logger, cts.Token),
                Command.Autoflash => await Commands.AutoflashAsync(serviceFactory, p, logger, cts.Token),
                Command.Verb => await p.Verb!.RunAsync(p.VerbArgs, loggerFactory, cts.Token),
                _ => ExitCodes.Usage,
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitCodes.OperationFailed;
        }
    }

    private static string InformationalVersion() =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal static ParseResult Parse(string[] args, IReadOnlyList<CliVerb>? verbs = null)
    {
        if (args.Length == 0)
            return Ok(new Parsed { Command = Command.List });

        string verb = args[0];
        switch (verb)
        {
            case "-h" or "--help" or "help":
                return Ok(new Parsed { Command = Command.Help });
            case "--version":
                return Ok(new Parsed { Command = Command.Version });
        }

        Command command;
        int start;
        if (verb == "list") { command = Command.List; start = 1; }
        else if (verb == "flash") { command = Command.Flash; start = 1; }
        else if (verb == "autoflash") { command = Command.Autoflash; start = 1; }
        else if (FindVerb(verbs, verb) is { } extra)
        {
            // A front-end verb: route the rest to it, which parses its own arguments. The one
            // exception is --verbose, which the seam OWNS — it installs the log sink process-wide
            // before dispatch, so it both reads the flag and removes it. A verb therefore never sees
            // it and cannot disagree with the run loop about whether it was asked for.
            string[] rest = args[1..];
            bool verbose = Array.Exists(rest, IsVerboseFlag);
            return Ok(new Parsed
            {
                Command = Command.Verb,
                Verb = extra,
                VerbArgs = verbose ? Array.FindAll(rest, a => !IsVerboseFlag(a)) : rest,
                Verbose = verbose,
            });
        }
        else if (verb.StartsWith('-'))
        {
            // Flags with no verb -> list. But if a verb name appears later in the line, the user
            // put a global flag first; say so instead of reporting it as an unknown list option.
            for (int i = 1; i < args.Length; i++)
            {
                if (FindVerb(verbs, args[i]) is not null || args[i] is "list" or "flash" or "autoflash")
                    return Err($"Global flags must come after the command: try '{args[i]} … {verb}'.");
            }
            command = Command.List; start = 0;
        }
        else return Err($"Unknown command '{verb}'.");

        var p = new Parsed { Command = command };
        for (int i = start; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--file" or "-f":
                    if (++i >= args.Length) return Err("--file requires a path.");
                    p = p with { File = args[i] };
                    break;
                case "--target" or "-t":
                    if (++i >= args.Length) return Err("--target requires a device id.");
                    p = p with { Target = args[i] };
                    break;
                case "--base" or "-b":
                    if (++i >= args.Length) return Err("--base requires an address.");
                    if (!TryParseAddress(args[i], out uint addr)) return Err($"Invalid --base address '{args[i]}'.");
                    p = p with { BaseAddress = addr };
                    break;
                case "--family":
                    if (++i >= args.Length) return Err("--family requires a name.");
                    p = p with { Family = args[i] };
                    break;
                case "--bootloader-timeout":
                    if (++i >= args.Length) return Err("--bootloader-timeout requires a number of seconds.");
                    if (!TryParseSeconds(args[i], out var bootTimeout))
                        return Err($"Invalid --bootloader-timeout '{args[i]}'; expected seconds greater than 0 (e.g. 45).");
                    p = p with { BootloaderTimeout = bootTimeout };
                    break;
                case "--all": p = p with { All = true }; break;
                case "--yes" or "-y": p = p with { Yes = true }; break;
                case "--no-leave": p = p with { NoLeave = true }; break;
                case "--no-verify": p = p with { NoVerify = true }; break;
                case "--verbose" or "-v": p = p with { Verbose = true }; break;
                case "-h" or "--help": return Ok(new Parsed { Command = Command.Help });
                default: return Err($"Unknown option '{a}'.");
            }
        }

        if (p.Command == Command.Flash && string.IsNullOrWhiteSpace(p.File))
            return Err("flash requires --file <path>.");
        if (p.Command == Command.Autoflash && string.IsNullOrWhiteSpace(p.File))
            return Err("autoflash requires --file <path>.");
        if (p.All && p.Target is not null)
            return Err("--all and --target are mutually exclusive.");

        return Ok(p);

        static ParseResult Ok(Parsed value) => new(value, null);
        static ParseResult Err(string message) => new(null, message);
    }

    /// <summary>
    /// Parses a positive, finite number of seconds (decimals allowed, e.g. <c>2.5</c>) into a
    /// <see cref="TimeSpan"/>. Zero, negative, non-finite and unparseable input all fail.
    /// </summary>
    public static bool TryParseSeconds(string s, out TimeSpan value)
    {
        value = default;
        if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return false;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > MaxTimeoutSeconds)
            return false;
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Upper bound on a CLI-supplied timeout (24h) — past this, a typo is likelier than an intent.</summary>
    private const double MaxTimeoutSeconds = 24 * 60 * 60;

    public static bool TryParseAddress(string s, out uint value)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>True for the global verbosity flag, which the seam owns and strips from a verb's tail.</summary>
    private static bool IsVerboseFlag(string arg) => arg is "-v" or "--verbose";

    /// <summary>Finds the front-end verb <paramref name="name"/> names, or <c>null</c>. Pure and total.</summary>
    private static CliVerb? FindVerb(IReadOnlyList<CliVerb>? verbs, string name)
    {
        if (verbs is null) return null;
        foreach (var v in verbs)
            if (string.Equals(v.Name, name, StringComparison.Ordinal))
                return v;
        return null;
    }

    public static string HelpText(string toolName, string banner, IReadOnlyList<CliVerb>? verbs = null) =>
        $"""
        {toolName} - {banner}

        USAGE
          {toolName} [list]
              List detected flashable targets (default).
          {toolName} flash --file <path> [opts]
              Flash firmware to a detected target.
          {toolName} autoflash --file <path> [opts]
              Arm hands-free flashing: flash matching devices as they are plugged in, until Ctrl+C.
        {VerbUsage(toolName, verbs)}
        OPTIONS
          -f, --file <path>     Firmware image: .bin (raw), .hex (Intel HEX), or .elf (ELF).
                                .dfu not yet supported.
          -t, --target <id>     (flash) A specific target by id (else the only one; or --all).
              --all             (flash) Flash every detected target.
              --family <name>   (autoflash) Provider/family to auto-flash; default the only one.
          -b, --base <addr>     Base address for a raw .bin (hex 0x.. or decimal); ignored for
                                .hex / .elf (they carry their own addresses).
                                Default 0x08000000 (STM32 flash).
              --no-leave        Do not leave the bootloader / start the app after flashing.
              --no-verify       Skip read-back verification.
              --bootloader-timeout <s>
                                Seconds to wait for a rebooted board's bootloader to re-enumerate
                                before giving up (application-mode targets only). Default 15.
                                Raise it to tell a slow board apart from one that never rebooted.
          -v, --verbose         Log discovery + flash detail to stderr.
          -y, --yes             Actually flash / arm. Without it, the command is a DRY RUN.
          -h, --help            Show this help.
              --version         Show the tool version.
        {VerbOptions(verbs)}
        EXIT CODES
          0 ok / clean dry run   1 a flash failed   2 usage   3 no firmware image   4 no target
        """;

    /// <summary>The USAGE entries contributed by the front-end's verbs (empty when there are none).</summary>
    private static string VerbUsage(string toolName, IReadOnlyList<CliVerb>? verbs)
    {
        if (verbs is null || verbs.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var v in verbs)
            sb.Append("  ").Append(toolName).Append(' ').Append(v.Usage).Append('\n')
              .Append("      ").Append(v.Summary).Append('\n');
        return sb.ToString();
    }

    /// <summary>The extra OPTIONS blocks contributed by the front-end's verbs (empty when there are none).</summary>
    private static string VerbOptions(IReadOnlyList<CliVerb>? verbs)
    {
        if (verbs is null) return "";
        var sb = new StringBuilder();
        foreach (var v in verbs)
        {
            if (string.IsNullOrEmpty(v.OptionsHelp)) continue;
            sb.Append('\n').Append(v.OptionsHelp);
            if (!v.OptionsHelp.EndsWith('\n')) sb.Append('\n');
        }
        return sb.ToString();
    }
}
