// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Periphery.Cli.Rendering;
using Periphery.Monitor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery monitor list</c> — one row per connected monitor with its
/// control-plane availability and live state. Each row is an ADR-0026
/// Option D snapshot (<see cref="MonitorDevice.ReadCapabilitiesAsync"/>):
/// a transient handle per monitor, I/O cost explicit and visible.
/// </summary>
/// <remarks>
/// Unlike <c>monitor layout</c>, this command <b>opens a handle per monitor</b>.
/// A <c>--json</c> run is therefore a real DDC/CI exercise of the rig, not a
/// zero-cost read — which is the point of including it in a smoke check, and the
/// reason a per-monitor failure is reported as a row rather than aborting.
/// </remarks>
internal sealed class MonitorListCommand : AsyncCommand<MonitorListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Emit one JSON object per monitor instead of a formatted table.")]
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Progress chatter would corrupt a piped JSON payload, so it is suppressed
        // in --json mode rather than sent to stderr: these lines are UI, and a
        // smoke check redirecting stderr wants failures there, not status.
        if (!settings.Json)
            AnsiConsole.MarkupLine("[grey]Enumerating monitors…[/]");

        var monitors = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Monitor)
            .ToListAsync(cancellationToken);

        if (settings.Json)
        {
            var reports = new List<MonitorCapabilityReport>(monitors.Count);
            foreach (var monitor in monitors)
            {
                try
                {
                    var snapshot = await MonitorDevice.ReadCapabilitiesAsync(monitor, cancellationToken);
                    reports.Add(new MonitorCapabilityReport(
                        monitor.Id,
                        monitor.Name,
                        snapshot.SupportsVcp,
                        snapshot.SupportsDisplayMode,
                        snapshot.CurrentMode?.ToString(),
                        snapshot.Orientation?.ToString(),
                        snapshot.Capabilities?.MccsVersion,
                        snapshot.Capabilities?.Model,
                        Error: null));
                }
                // Deliberately broader than the table path's `catch (MonitorException)`.
                // A smoke check on a flaky rig is the case that matters: a
                // misbehaving driver can surface a COMException or an
                // InvalidOperationException, and letting that abort the command
                // would emit NO json at all — losing the good rows too, and making
                // a driver fault look like an absent device. Cancellation is
                // rethrown so Ctrl-C still stops the run promptly.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reports.Add(new MonitorCapabilityReport(
                        monitor.Id, monitor.Name,
                        SupportsVcp: null, SupportsDisplayMode: null,
                        CurrentMode: null, Orientation: null,
                        MccsVersion: null, Model: null,
                        Error: $"{ex.GetType().Name}: {ex.Message}"));
                }
            }

            var opts = new JsonSerializerOptions(MonitorCapabilityJsonContext.Default.Options)
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            // The source generator emits this options-taking constructor; it is how
            // custom options (indented, relaxed escaping) get bound to GetTypeInfo.
            var ctx = new MonitorCapabilityJsonContext(opts);
            using var stdout = Console.OpenStandardOutput();
            await JsonSerializer.SerializeAsync(
                stdout, reports, ctx.GetTypeInfo(typeof(List<MonitorCapabilityReport>))!, cancellationToken);
            return 0;
        }

        if (monitors.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No monitors enumerated.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            "[grey]Reading control capabilities (one transient handle per monitor)…[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("VCP");
        table.AddColumn("Mode ctl");
        table.AddColumn("Current");
        table.AddColumn("Orientation");
        table.AddColumn("MCCS");
        table.AddColumn("Model");

        foreach (var monitor in monitors)
        {
            string name = Markup.Escape(monitor.Name ?? monitor.Id);
            try
            {
                var snapshot = await MonitorDevice.ReadCapabilitiesAsync(monitor, cancellationToken);
                table.AddRow(
                    name,
                    snapshot.SupportsVcp ? "[green]yes[/]" : "[grey]no[/]",
                    snapshot.SupportsDisplayMode ? "[green]yes[/]" : "[grey]no[/]",
                    snapshot.CurrentMode?.ToString() ?? "—",
                    snapshot.Orientation?.ToString() ?? "—",
                    snapshot.Capabilities?.MccsVersion ?? "—",
                    Markup.Escape(snapshot.Capabilities?.Model ?? "—"));
            }
            catch (MonitorException ex)
            {
                table.AddRow(name, "[red]err[/]", "[red]err[/]",
                    Markup.Escape(ex.Message), "—", "—", "—");
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"{monitors.Count} monitor(s).");
        return 0;
    }
}
