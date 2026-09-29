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
    internal static Task<DeviceInfo?> ResolveMonitorAsync(
        string? id, string? name, CancellationToken ct)
        => DeviceSelection.ResolveAsync(
            AnsiConsole.Console, DeviceCategory.Monitor, "monitor", "periphery monitor list",
            id, name, ct);

    internal static int Fail(MonitorException ex)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        if (ex.InnerException is not null)
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(ex.InnerException.Message)}[/]");
        return 1;
    }
}
