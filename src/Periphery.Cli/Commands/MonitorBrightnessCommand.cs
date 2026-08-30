// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Globalization;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor set-brightness &lt;percent&gt;</c> — DDC/CI luminance,
/// normalized over the panel's reported maximum.
/// </summary>
internal sealed class MonitorBrightnessCommand : AsyncCommand<MonitorBrightnessCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Brightness percent, 0-100.")]
        [CommandArgument(0, "<PERCENT>")]
        public int Percent { get; init; }

        [Description("Select by name substring (when several monitors are connected).")]
        [CommandOption("--name")]
        public string? Name { get; init; }

        [Description("Select by exact device Id.")]
        [CommandOption("--id")]
        public string? Id { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Percent is < 0 or > 100)
        {
            AnsiConsole.MarkupLine("[red]Percent must be 0-100.[/]");
            return 1;
        }

        var target = await MonitorCommandHelpers.ResolveMonitorAsync(
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        try
        {
            await using var monitor = await MonitorDevice.OpenAsync(target, cancellationToken);
            await monitor.SetBrightnessAsync(settings.Percent / 100d, cancellationToken);
            double readBack = await monitor.GetBrightnessAsync(cancellationToken);
            AnsiConsole.MarkupLine(
                $"[green]Brightness set.[/] [grey]{Markup.Escape(target.Name ?? target.Id)} reads back[/] "
                + $"[white]{readBack.ToString("P0", CultureInfo.InvariantCulture)}[/]");
            return 0;
        }
        catch (MonitorException ex)
        {
            return MonitorCommandHelpers.Fail(ex);
        }
    }
}
