// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Linq;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Periphery.Monitor;
using Periphery.Monitor.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor layout</c> — the whole-topology snapshot
/// (ADR-0059 read surface): identity, current vs preferred mode, rotation,
/// output technology, position, primary. Zero device handles.
/// </summary>
/// <remarks>
/// <c>--json</c> makes this usable as a smoke-check probe on a VM or a hardware
/// rig: the payload is the whole <see cref="MonitorLayout"/> with enums by name,
/// so a run can be diffed or asserted on without scraping a table. It is the only
/// way to observe <see cref="MonitorLayoutEntry.OutputTechnology"/> off-box, which
/// is what an IddCx / DisplayLink measurement needs (ADR-0070 D2, ADR-0072).
/// </remarks>
internal sealed class MonitorLayoutCommand : AsyncCommand<MonitorLayoutCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Emit the raw layout snapshot as JSON instead of a formatted table.")]
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var layout = await MonitorLayout.ReadAsync(cancellationToken);

        if (settings.Json)
        {
            // Emitted even when empty: "no active display paths" is itself a
            // result a smoke check wants to record (headless / session 0 / LTSC
            // zero-paths), not an error to special-case. Relaxed escaping keeps
            // device instance ids copy-pasteable, matching `devices list --json`.
            var opts = new JsonSerializerOptions(MonitorLayoutJsonContext.Default.Options)
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var ctx = new MonitorLayoutJsonContext(opts);
            using var stdout = Console.OpenStandardOutput();
            await JsonSerializer.SerializeAsync(
                stdout, layout, ctx.GetTypeInfo(typeof(MonitorLayout))!, cancellationToken);

            // The payload is still valid JSON and carries `availability`, but a
            // caller that only checks the exit code must not record a
            // session-blind read as a true empty topology.
            return layout.Availability == MonitorLayoutAvailability.NotVisibleFromThisSession
                ? 2
                : 0;
        }

        if (layout.Monitors.IsEmpty)
        {
            // Say WHICH empty this is. Reporting session-0 blindness as "headless"
            // sends an operator to check cables on a machine whose displays are
            // fine (issue #207).
            if (layout.Availability == MonitorLayoutAvailability.NotVisibleFromThisSession)
            {
                AnsiConsole.MarkupLine(
                    "[red]Displays are not visible from this session.[/] [grey]This process is in "
                    + "session 0 (a Windows service, or an SSH shell), where display configuration "
                    + "does not exist — monitors may well be attached and working. Re-run from the "
                    + "interactive session. This is NOT a headless machine.[/]");
                return 2;
            }

            AnsiConsole.MarkupLine(
                "[yellow]No active display paths.[/] [grey](Genuinely headless, every output "
                + "disabled, or LTSC zero-paths — see ADR-0059 D4.)[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Primary");
        table.AddColumn("Current");
        table.AddColumn("Preferred");
        table.AddColumn("Orientation");
        table.AddColumn("Output");
        table.AddColumn("Panel (EDID claim)");
        table.AddColumn("Position");
        table.AddColumn("Id");

        foreach (var entry in layout.Monitors)
        {
            table.AddRow(
                Markup.Escape(entry.FriendlyName ?? "—"),
                entry.IsPrimary ? "[green]yes[/]" : "[grey]no[/]",
                entry.CurrentMode.ToString(),
                entry.PreferredMode?.ToString() ?? "—",
                entry.Orientation.ToString(),
                FormatOutputTechnology(entry.OutputTechnology),
                // Rendered dim, and headed "claim", because this column is the
                // one an operator will use to conclude "that screen is fake" —
                // the exact verdict ADR-0073 D1 commits Periphery to never
                // synthesize. The XML docs say fingerprint-not-fact, but a doc is
                // not the surface an operator reads; the table is.
                $"[grey]{Markup.Escape(entry.PanelId?.PnpId ?? "—")}[/]",
                entry.Position.ToString(),
                Markup.Escape(entry.DeviceId));
        }

        AnsiConsole.Write(table);

        if (layout.Monitors.Any(e => e.PanelId is not null))
        {
            AnsiConsole.MarkupLine(
                "[grey]Panel is the EDID identity the display [italic]claims[/], not a verified fact. "
                + "It is the best available fingerprint for recognising a known synthetic display "
                + "(e.g. IddSampleDriver reports LNX0000), but a match is a fingerprint, not proof — "
                + "and no column here answers \"is there a real panel\". See ADR-0073.[/]");
        }

        return 0;
    }

    /// <summary>
    /// Highlights the two indirect technologies, which are the ones a smoke check
    /// on a rig is actually looking for — and which must stay visibly distinct,
    /// since neither alone means "this screen is virtual" (ADR-0072).
    /// </summary>
    private static string FormatOutputTechnology(MonitorOutputTechnology tech) => tech switch
    {
        MonitorOutputTechnology.IndirectWired => "[yellow]IndirectWired[/]",
        MonitorOutputTechnology.IndirectVirtual => "[magenta]IndirectVirtual[/]",
        MonitorOutputTechnology.Other => "[grey]Other[/]",
        _ => tech.ToString(),
    };
}
