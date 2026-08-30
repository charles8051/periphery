// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor set-orientation &lt;orientation&gt;</c> — rotates the
/// output, persisting by default (provisioning audience; the landscape /
/// portrait width-height swap is handled by the library).
/// </summary>
internal sealed class MonitorSetOrientationCommand : AsyncCommand<MonitorSetOrientationCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("landscape | portrait | landscape-flipped | portrait-flipped")]
        [CommandArgument(0, "<ORIENTATION>")]
        public string Orientation { get; init; } = string.Empty;

        [Description("Apply for this session only (do not persist to the registry).")]
        [CommandOption("--no-persist")]
        public bool NoPersist { get; init; }

        [CommandOption("--name")]
        public string? Name { get; init; }

        [CommandOption("--id")]
        public string? Id { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        MonitorOrientation? orientation = settings.Orientation.ToLowerInvariant() switch
        {
            "landscape" => MonitorOrientation.Landscape,
            "portrait" => MonitorOrientation.Portrait,
            "landscape-flipped" => MonitorOrientation.LandscapeFlipped,
            "portrait-flipped" => MonitorOrientation.PortraitFlipped,
            _ => null,
        };
        if (orientation is null)
        {
            AnsiConsole.MarkupLine(
                "[red]ORIENTATION must be landscape, portrait, landscape-flipped, or portrait-flipped.[/]");
            return 1;
        }

        var target = await MonitorCommandHelpers.ResolveMonitorAsync(
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        try
        {
            await using var monitor = await MonitorDevice.OpenAsync(target, cancellationToken);
            await monitor.SetOrientationAsync(
                orientation.Value, persist: !settings.NoPersist, cancellationToken);
            var mode = await monitor.GetCurrentModeAsync(cancellationToken);
            AnsiConsole.MarkupLine(
                $"[green]Orientation set to {orientation}.[/] "
                + $"[grey]{Markup.Escape(target.Name ?? target.Id)} now at[/] [white]{mode}[/]"
                + $"{(settings.NoPersist ? " [grey](session only)[/]" : "")}");
            return 0;
        }
        catch (MonitorException ex)
        {
            return MonitorCommandHelpers.Fail(ex);
        }
    }
}
