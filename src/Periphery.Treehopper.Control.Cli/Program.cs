// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Control;
using Periphery.Treehopper.Control.Cli;

var parse = Cli.Parse(args);
if (parse.Error is not null)
{
    Console.Error.WriteLine(parse.Error);
    return ExitCodes.Usage;
}

var p = parse.Value!;
if (p.Kind == CommandKind.Help) { PrintHelp(); return ExitCodes.Success; }
if (p.Kind == CommandKind.Version) { Console.WriteLine(InformationalVersion()); return ExitCodes.Success; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return p.Kind switch
    {
        CommandKind.List => await Commands.ListAsync(p, cts.Token),
        CommandKind.Watch => await Commands.WatchAsync(p, cts.Token),
        CommandKind.Pin => await Commands.PinAsync(p, cts.Token),
        CommandKind.I2c => await Commands.I2cAsync(p, cts.Token),
        CommandKind.FirmwareAll => await Commands.FirmwareAsync(p, all: true, cts.Token),
        CommandKind.FirmwareBoard => await Commands.FirmwareAsync(p, all: false, cts.Token),
        _ => ExitCodes.Usage,
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return ExitCodes.FlashFailed;
}

static string InformationalVersion() =>
    Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

static void PrintHelp()
{
    int? embedded = FirmwareSource.EmbeddedVersion();
    Console.WriteLine(
$@"treehopper — control app for Treehopper boards (list, GPIO, I2C, firmware)

USAGE
  treehopper [list]                              List boards + version + firmware status (read-only; default).
  treehopper watch [serial] [--seconds N]        Live pin grid for one board (streams; Ctrl+C to stop).
  treehopper pin <serial> <0-19> <action>        action: high | low | output | input | analog
  treehopper i2c <serial>                        Scan the I2C bus and list responders.
  treehopper firmware all  [--yes]               Fleet update: flash every board that needs it.
  treehopper firmware board <serial> [--yes]     Update one board.

  Firmware commands without --yes are a DRY RUN (plan only, no writes). Add --yes to flash.

OPTIONS
  --file <path>            Firmware image to flash instead of the embedded one. Accepts a
                           .hex (Intel HEX) or .tfi/.efm8 (boot records); the format is
                           inferred from the extension and verified against the content.
  --target-version <code>  Target firmware version (decimal 274 or hex 0x0112). Gates updates.
  --force                  Flash regardless of version.
  --json                   Machine-readable output.
  --seconds <n>            For 'watch': stop after n seconds (else runs until Ctrl+C).
  -h, --help / --version   Help / tool version.

FIRMWARE IMAGE
  Embedded image: {(embedded is int v ? $"present, target {FirmwareVersion.Describe(v)}" : "none in this build")}.
  A fleet build embeds it: dotnet publish ... -p:TreehopperFirmwareImage=<path> -p:TreehopperFirmwareVersion=<code>

EXIT CODES
  0 ok / clean dry run   1 an operation failed   2 usage/refused   3 no firmware image   4 no board

EXAMPLES
  treehopper                                     # list boards on this host
  treehopper pin TH-00042 3 high                 # drive pin 3 high
  treehopper i2c TH-00042                         # scan the I2C bus
  treehopper firmware all                         # preview a fleet update
  treehopper firmware all --yes --json            # update out-of-date boards (fleet/SSH)");
}
