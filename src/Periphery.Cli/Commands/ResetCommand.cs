// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using Periphery.Cli.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery devices reset &lt;device-id&gt;</c> — cycle a device's transport via the platform
/// <see cref="IDeviceReset"/> (ADR-0060): a PnP disable/enable or a USB port re-enumeration.
/// Designed to pair with <c>devices watch --verbose</c> in another terminal: run the reset here,
/// watch the OS plumbing react there (does the device re-enumerate, or not?).
/// </summary>
internal sealed class ResetCommand : AsyncCommand<ResetCommand.Settings>
{
    public sealed class Settings : DiagnosticSettings
    {
        [Description("Device ID (from `devices list`). Quote if it contains backslashes.")]
        [CommandArgument(0, "<DEVICE_ID>")]
        public string DeviceId { get; init; } = string.Empty;

        [Description("Strategy: SoftProtocol, SoftProtocolOutOfBand, UsbPortCycle, or PnpDisableEnable. Default: the gentlest available.")]
        [CommandOption("-s|--strategy <KIND>")]
        public ResetKind? Strategy { get; init; }

        [Description("List the device's available reset strategies and exit (no reset performed).")]
        [CommandOption("--list")]
        public bool List { get; init; }

        [Description("Show what would run without performing the reset.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        settings.ApplyLogging();

        // Resolve to the enriched snapshot when possible (nicer output, correct enumerator);
        // fall back to a raw instance id so a GaveUp / unenumerable device can still be targeted.
        // announceOpen:false — reset prints its own "Device:" line below instead of "Opening …".
        var device = await HidCommandHelpers.ResolveDeviceAsync(
            settings.DeviceId, cancellationToken,
            announceOpen: false, rawVerb: "using", rawNoun: "instance id");

        var reset = DeviceReset.PlatformDefault;
        var strategies = reset.StrategiesFor(device);

        AnsiConsole.MarkupLine($"[grey]Device:[/] [white]{Markup.Escape(device.Name ?? device.Id)}[/]");

        if (strategies.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Not resettable[/] — no reset strategies for this device (non-USB / virtual, or no USB ancestor resolved).");
            return 1;
        }

        AnsiConsole.MarkupLine("[grey]Available (gentlest first):[/] " + string.Join(
            ", ",
            strategies.Select(s => $"[white]{s.Kind}[/] [dim](reenum={s.ReEnumerates}, radius={s.Radius})[/]")));

        if (settings.List)
            return 0;

        ResetStrategy chosen;
        if (settings.Strategy is { } kind)
        {
            if (!strategies.Any(s => s.Kind == kind))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Strategy {kind} is not available for this device.[/] Use --list to see the options.");
                return 1;
            }
            chosen = strategies.First(s => s.Kind == kind);
        }
        else
        {
            chosen = strategies[0];   // gentlest-first per the IDeviceReset contract
        }

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Dry run:[/] would reset via [white]{chosen.Kind}[/] [dim](reenum={chosen.ReEnumerates})[/]. No action taken.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[grey]Resetting via[/] [white]{chosen.Kind}[/][grey] …[/]");
        var outcome = await reset.ResetAsync(device, chosen, cancellationToken);

        var (glyph, color) = outcome switch
        {
            ResetOutcome.Issued       => ("✓", "green"),
            ResetOutcome.Degraded     => ("≈", "yellow"),
            ResetOutcome.Failed       => ("✗", "red"),
            ResetOutcome.NotSupported => ("∅", "yellow"),
            _                         => ("?", "grey"),
        };
        AnsiConsole.MarkupLine($"[{color}]{glyph} {outcome}[/]");

        if (outcome == ResetOutcome.Failed)
            AnsiConsole.MarkupLine(
                "[grey]Hint: CM_Disable/Enable_DevNode usually require an elevated (admin) shell.[/]");

        return outcome is ResetOutcome.Issued or ResetOutcome.Degraded ? 0 : 1;
    }
}
