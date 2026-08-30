// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Globalization;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor set-resolution &lt;WIDTHxHEIGHT[@HZ]&gt;</c> — sets
/// the display mode, persisting to the registry by default (this command's
/// audience is provisioning; pass <c>--no-persist</c> for a session-scoped
/// change). ADR-0058 OQ-005: the library API stays explicit, the CLI is
/// opinionated.
/// </summary>
internal sealed class MonitorSetResolutionCommand : AsyncCommand<MonitorSetResolutionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Target mode, e.g. 1920x1080 or 720x1280@60.")]
        [CommandArgument(0, "<MODE>")]
        public string Mode { get; init; } = string.Empty;

        [Description("Apply for this session only (do not persist to the registry).")]
        [CommandOption("--no-persist")]
        public bool NoPersist { get; init; }

        [Description("Route through the ADR-0059 layout applier (CCD transaction) instead of the per-monitor handle.")]
        [CommandOption("--via-layout")]
        public bool ViaLayout { get; init; }

        [CommandOption("--name")]
        public string? Name { get; init; }

        [CommandOption("--id")]
        public string? Id { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!TryParseMode(settings.Mode, out var mode))
        {
            AnsiConsole.MarkupLine("[red]MODE must look like 1920x1080 or 720x1280@60.[/]");
            return 1;
        }

        var target = await MonitorCommandHelpers.ResolveMonitorAsync(
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        try
        {
            if (settings.ViaLayout)
            {
                var result = await MonitorLayoutApplier.ApplyAsync(
                    [new MonitorConfiguration(target.Id, Mode: mode)],
                    new MonitorLayoutApplyOptions { Persist = !settings.NoPersist },
                    cancellationToken);
                AnsiConsole.MarkupLine(result.Outcome == MonitorLayoutApplyOutcome.AlreadySatisfied
                    ? "[green]Already at the requested mode[/] [grey](layout applier no-op).[/]"
                    : $"[green]Mode set via layout applier.[/] [grey]now[/] [white]{result.Layout.Monitors.FirstOrDefault(m => string.Equals(m.DeviceId, target.Id, StringComparison.OrdinalIgnoreCase))?.CurrentMode}[/]");
                return 0;
            }

            await using var monitor = await MonitorDevice.OpenAsync(target, cancellationToken);
            await monitor.SetModeAsync(mode, persist: !settings.NoPersist, cancellationToken);
            var applied = await monitor.GetCurrentModeAsync(cancellationToken);
            AnsiConsole.MarkupLine(
                $"[green]Mode set.[/] [grey]{Markup.Escape(target.Name ?? target.Id)} now at[/] "
                + $"[white]{applied}[/]{(settings.NoPersist ? " [grey](session only)[/]" : "")}");
            return 0;
        }
        catch (MonitorException ex)
        {
            return MonitorCommandHelpers.Fail(ex);
        }
    }

    internal static bool TryParseMode(string text, out DisplayMode mode)
    {
        mode = new DisplayMode(0, 0, 0);
        int hz = 0;

        int at = text.IndexOf('@');
        string dims = at >= 0 ? text[..at] : text;
        if (at >= 0 && !int.TryParse(text.AsSpan(at + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out hz))
            return false;

        int x = dims.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (x <= 0
            || !int.TryParse(dims.AsSpan(..x), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(dims.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            || width <= 0 || height <= 0)
            return false;

        mode = new DisplayMode(width, height, hz);
        return true;
    }
}
