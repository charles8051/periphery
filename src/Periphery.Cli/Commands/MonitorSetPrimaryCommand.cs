// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor set-primary</c> — designates a monitor as primary via
/// the ADR-0059 layout applier (a whole-topology translation transaction).
/// Idempotent and persisted by default.
/// </summary>
internal sealed class MonitorSetPrimaryCommand : AsyncCommand<MonitorSetPrimaryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Apply for this session only (do not persist).")]
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
        var target = await MonitorCommandHelpers.ResolveMonitorAsync(
            settings.Id, settings.Name, cancellationToken);
        if (target is null) return 1;

        try
        {
            var result = await MonitorLayoutApplier.ApplyAsync(
                [new MonitorConfiguration(target.Id, IsPrimary: true)],
                new MonitorLayoutApplyOptions { Persist = !settings.NoPersist },
                cancellationToken);

            AnsiConsole.MarkupLine(result.Outcome == MonitorLayoutApplyOutcome.AlreadySatisfied
                ? $"[green]Already primary[/] [grey]— no change applied.[/]"
                : $"[green]Primary set.[/] [grey]{Markup.Escape(target.Name ?? target.Id)} now at (0,0).[/]");
            return 0;
        }
        catch (MonitorException ex)
        {
            return MonitorCommandHelpers.Fail(ex);
        }
    }
}
