// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Monitor;
using Spectre.Console;

namespace Periphery.Cli.Commands;

/// <summary>
/// Shared target selection for the <c>monitor</c> command group: pick one
/// monitor by exact Id, by name substring, or implicitly when exactly one is
/// connected.
/// </summary>
internal static class MonitorCommandHelpers
{
    internal static async Task<DeviceInfo?> ResolveMonitorAsync(
        string? id, string? name, CancellationToken ct)
    {
        var monitors = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Monitor)
            .ToListAsync(ct);

        if (id is not null)
        {
            var byId = monitors.FirstOrDefault(m =>
                string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is null)
                AnsiConsole.MarkupLine(
                    $"[red]No monitor with Id '{Markup.Escape(id)}'. "
                    + "Run `periphery monitor list` for the connected set.[/]");
            return byId;
        }

        if (name is not null)
        {
            var byName = monitors
                .Where(m => m.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            if (byName.Count == 1)
                return byName[0];
            AnsiConsole.MarkupLine(byName.Count == 0
                ? $"[red]No monitor name contains '{Markup.Escape(name)}'.[/]"
                : $"[red]'{Markup.Escape(name)}' matches {byName.Count} monitors — narrow it or use --id.[/]");
            return null;
        }

        if (monitors.Count == 1)
            return monitors[0];

        AnsiConsole.MarkupLine(monitors.Count == 0
            ? "[red]No monitors enumerated.[/]"
            : $"[red]{monitors.Count} monitors connected — pick one with --name or --id "
              + "(see `periphery monitor list`).[/]");
        return null;
    }

    internal static int Fail(MonitorException ex)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        if (ex.InnerException is not null)
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(ex.InnerException.Message)}[/]");
        return 1;
    }
}
