// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Periphery.Treehopper.Control.Cli;

internal enum CommandKind { List, Watch, Pin, I2c, FirmwareAll, FirmwareBoard, Help, Version }

/// <summary>Parsed, validated command line.</summary>
internal sealed record Parsed
{
    public CommandKind Kind { get; init; }
    public string? Selector { get; init; }      // serial or device id (pin/i2c/watch/firmware board)
    public int Pin { get; init; }
    public string? PinAction { get; init; }       // high|low|input|output|analog
    public bool Json { get; init; }
    public bool Yes { get; init; }
    public bool Force { get; init; }
    public string? FilePath { get; init; }
    public int? TargetVersion { get; init; }
    public int? Seconds { get; init; }
}

internal readonly record struct ParseResult(Parsed? Value, string? Error)
{
    public static ParseResult Ok(Parsed p) => new(p, null);
    public static ParseResult Fail(string e) => new(null, e);
}

/// <summary>Hand-rolled parser for the slim <c>treehopper</c> CLI. Pure and total.</summary>
internal static class Cli
{
    public static ParseResult Parse(string[] args)
    {
        bool json = false, yes = false, force = false;
        string? file = null;
        int? target = null, seconds = null;
        var pos = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help": return ParseResult.Ok(new Parsed { Kind = CommandKind.Help });
                case "--version": return ParseResult.Ok(new Parsed { Kind = CommandKind.Version });
                case "--json": json = true; break;
                case "-y" or "--yes": yes = true; break;
                case "--force": force = true; break;
                case "--file":
                    if (!Take(args, ref i, out file)) return ParseResult.Fail("--file requires a path.");
                    break;
                case "--target-version" or "--target":
                    if (!Take(args, ref i, out var tv)) return ParseResult.Fail($"{a} requires a version value.");
                    if (!FirmwareVersion.TryParse(tv, out var t))
                        return ParseResult.Fail($"Invalid --target-version '{tv}'. Use decimal (274) or hex (0x0112).");
                    target = t; break;
                case "--seconds":
                    if (!Take(args, ref i, out var sv)
                        || !int.TryParse(sv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) || s <= 0)
                        return ParseResult.Fail("--seconds must be a positive integer.");
                    seconds = s; break;
                default:
                    if (a.StartsWith('-')) return ParseResult.Fail($"Unknown option '{a}'. Run with --help.");
                    pos.Add(a); break;
            }
        }

        var b = new Parsed
        {
            Json = json, Yes = yes, Force = force, FilePath = file, TargetVersion = target, Seconds = seconds,
        };

        if (pos.Count == 0) return ParseResult.Ok(b with { Kind = CommandKind.List });

        switch (pos[0].ToLowerInvariant())
        {
            case "list":
                return ParseResult.Ok(b with { Kind = CommandKind.List });

            case "watch":
                return ParseResult.Ok(b with { Kind = CommandKind.Watch, Selector = pos.ElementAtOrDefault(1) });

            case "pin":
                if (pos.Count < 4)
                    return ParseResult.Fail("Usage: treehopper pin <serial> <pin> <high|low|input|output|analog>");
                if (!int.TryParse(pos[2], out int pin) || pin is < 0 or > 19)
                    return ParseResult.Fail("Pin must be 0-19.");
                string action = pos[3].ToLowerInvariant();
                if (action is not ("high" or "low" or "input" or "output" or "analog"))
                    return ParseResult.Fail("Action must be one of: high, low, input, output, analog.");
                return ParseResult.Ok(b with { Kind = CommandKind.Pin, Selector = pos[1], Pin = pin, PinAction = action });

            case "i2c":
                // Accept "i2c scan <serial>" or "i2c <serial>".
                string? sel = pos.Count >= 2 && pos[1].Equals("scan", System.StringComparison.OrdinalIgnoreCase)
                    ? pos.ElementAtOrDefault(2)
                    : pos.ElementAtOrDefault(1);
                if (sel is null) return ParseResult.Fail("Usage: treehopper i2c <serial>");
                return ParseResult.Ok(b with { Kind = CommandKind.I2c, Selector = sel });

            case "firmware":
                if (pos.Count < 2) return ParseResult.Fail("Usage: treehopper firmware <list|all|board> ...");
                switch (pos[1].ToLowerInvariant())
                {
                    case "list": return ParseResult.Ok(b with { Kind = CommandKind.List }); // alias of `list`
                    case "all": return ParseResult.Ok(b with { Kind = CommandKind.FirmwareAll });
                    case "board":
                        if (pos.Count < 3) return ParseResult.Fail("Usage: treehopper firmware board <serial>");
                        return ParseResult.Ok(b with { Kind = CommandKind.FirmwareBoard, Selector = pos[2] });
                    default: return ParseResult.Fail($"Unknown firmware subcommand '{pos[1]}'.");
                }

            default:
                return ParseResult.Fail($"Unknown command '{pos[0]}'. Run with --help.");
        }
    }

    private static bool Take(string[] args, ref int i, out string? value)
    {
        if (i + 1 >= args.Length) { value = null; return false; }
        value = args[++i];
        return true;
    }
}
