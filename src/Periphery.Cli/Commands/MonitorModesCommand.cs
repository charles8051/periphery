// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor modes</c> — every display mode the OS will accept
/// for one monitor's output, with the current mode highlighted.
/// </summary>
internal sealed class MonitorModesCommand : AsyncCommand<MonitorModesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--name")]
        public string? Name { get; init; }

        [CommandOption("--id")]
        public string? Id { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var target = await MonitorCommandHelpers.ResolveMonitorAsync(
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        try
        {
            await using var monitor = await MonitorDevice.OpenAsync(target, cancellationToken);
            var current = await monitor.GetCurrentModeAsync(cancellationToken);
            var orientation = await monitor.GetOrientationAsync(cancellationToken);
            var modes = await monitor.GetSupportedModesAsync(cancellationToken);

            AnsiConsole.MarkupLine(
                $"[grey]Current:[/] [white]{current}[/] [grey]({orientation})[/]");
            foreach (var group in modes
                         .GroupBy(m => (m.Width, m.Height))
                         .OrderByDescending(g => (long)g.Key.Width * g.Key.Height))
            {
                string rates = string.Join(", ", group
                    .Select(m => m.RefreshRateHz)
                    .OrderByDescending(hz => hz)
                    .Select(hz => hz.ToString()));
                string line = $"{group.Key.Width}x{group.Key.Height} @ {rates} Hz";
                bool isCurrent = group.Key == (current.Width, current.Height);
                AnsiConsole.MarkupLine(isCurrent ? $"[green]* {line}[/]" : $"  {line}");
            }
            AnsiConsole.MarkupLine($"{modes.Count} mode(s).");
            return 0;
        }
        catch (MonitorException ex)
        {
            return MonitorCommandHelpers.Fail(ex);
        }
    }
}
