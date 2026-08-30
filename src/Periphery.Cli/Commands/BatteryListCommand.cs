// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Runtime.Versioning;
using Periphery.Hid.Codecs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery battery list</c> — lists every device that exposes a
/// battery surface. Demonstrates the ADR-0026 two-piece pattern:
/// pure-metadata classification via <see cref="HidBatteryEnricher"/>
/// (auto-registered against core enumeration, tags HID UPSs as Battery),
/// then optional live state via <see cref="HidBattery.ReadSnapshotAsync"/>
/// (Option D static helper, performs I/O at the call site).
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
internal sealed class BatteryListCommand : AsyncCommand<BatteryListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Emit raw JSON of the (unmodified) DeviceInfo records instead of a formatted table. " +
                     "JSON output omits the live HID snapshot data — DeviceInfo is intentionally not " +
                     "mutated by snapshot reads (see ADR-0026).")]
        [CommandOption("--json")]
        public bool Json { get; init; }

        [Description("Skip the live HID battery snapshot reads — display tag/category info only, no I/O.")]
        [CommandOption("--no-snapshot")]
        public bool NoSnapshot { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[grey]Enumerating devices…[/]");
        // Core enumeration runs registered IDeviceEnrichers automatically
        // (ADR-0024 §3c); Periphery.Hid's HidBatteryEnricher registers
        // itself via [ModuleInitializer], so HID UPSs come back already
        // tagged with DeviceTags.Battery — no post-enumeration Select(Enrich)
        // dance required.
        var devices = await Devices.Enumerate().ToListAsync(cancellationToken);

        // Filter using ADR-0047 Option B (Tags or Category=Battery).
        // DeviceTags.Carries shares the rule with DeviceFilter.WithTag.
        var batteries = devices.Where(d => DeviceTags.Carries(d, DeviceTags.Battery)).ToList();

        if (settings.Json)
        {
            var opts = new System.Text.Json.JsonSerializerOptions(DeviceInfoJsonContext.Default.Options)
                { WriteIndented = true };
            using var stdout = Console.OpenStandardOutput();
            await System.Text.Json.JsonSerializer.SerializeAsync(
                stdout, batteries, typeof(List<DeviceInfo>), DeviceInfoJsonContext.Default, cancellationToken);
            return 0;
        }

        if (batteries.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey italic]No battery devices found.[/]");
            return 0;
        }

        // Step 3: live snapshot reads for HID devices (Option D static
        // helper — explicit I/O cost at the call site). System batteries
        // skip this; their fields are already populated on DeviceInfo
        // by the core WindowsBatteryEnricher during enumeration.
        var snapshots = new Dictionary<string, HidBatterySnapshot>();
        if (!settings.NoSnapshot)
        {
            AnsiConsole.MarkupLine("[grey]Reading live HID battery snapshots (one I/O round-trip per device)…[/]");
            foreach (var d in batteries.Where(d => d.Category == DeviceCategory.Hid))
            {
                try
                {
                    var snap = await HidBattery.ReadSnapshotAsync(d, cancellationToken);
                    if (snap is not null)
                        snapshots[d.Id] = snap.Value;
                }
                catch (HidException ex)
                {
                    AnsiConsole.MarkupLine(
                        $"  [yellow]snapshot failed for {Markup.Escape(d.Name ?? d.Id)}: " +
                        $"{Markup.Escape(ex.Message)}[/]");
                }
            }
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .AddColumn(new TableColumn("[bold grey]Name[/]"))
            .AddColumn(new TableColumn("[bold grey]Category[/]").Width(10))
            .AddColumn(new TableColumn("[bold grey]Charge[/]").Width(8))
            .AddColumn(new TableColumn("[bold grey]Status[/]").Width(12))
            .AddColumn(new TableColumn("[bold grey]AC[/]").Width(6))
            .AddColumn(new TableColumn("[bold grey]Low[/]").Width(6))
            .AddColumn(new TableColumn("[bold grey]Source[/]").Width(10))
            .AddColumn(new TableColumn("[bold grey]VID:PID[/]").Width(11));

        foreach (var d in batteries.OrderBy(d => d.Name ?? d.Id))
        {
            int? charge;
            BatteryStatus? status;
            bool? ac;
            bool? low;
            string source;

            if (snapshots.TryGetValue(d.Id, out var snap))
            {
                charge = snap.BatteryChargePercent;
                status = snap.BatteryStatus;
                ac = snap.IsExternalPowerConnected;
                low = snap.IsBatteryLow;
                source = "HID";
            }
            else
            {
                // Fall back to DeviceInfo fields — populated by core enrichers
                // for system batteries; null for HID UPSs when --no-snapshot
                // or when the snapshot read failed.
                charge = d.BatteryChargePercent;
                status = d.BatteryStatus;
                ac = d.IsExternalPowerConnected;
                low = d.IsBatteryLow;
                source = d.Category == DeviceCategory.Hid ? "—" : "OS";
            }

            var chargeStr = charge is int pct ? $"{pct}%" : "—";
            var statusStr = status?.ToString() ?? "—";
            var acStr = ac switch
            {
                true => "[green]yes[/]",
                false => "[yellow]no[/]",
                null => "[grey]—[/]",
            };
            var lowStr = low switch
            {
                true => "[red]yes[/]",
                false => "[green]no[/]",
                null => "[grey]—[/]",
            };
            var vidPid = d.VendorId is not null && d.ProductId is not null
                ? $"{d.VendorId}:{d.ProductId}"
                : "";

            table.AddRow(
                $"[white]{Markup.Escape(d.Name ?? d.Id)}[/]",
                $"[grey]{d.Category}[/]",
                $"[white]{chargeStr}[/]",
                $"[white]{Markup.Escape(statusStr)}[/]",
                acStr,
                lowStr,
                $"[dim]{source}[/]",
                $"[dim]{Markup.Escape(vidPid)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{batteries.Count} battery device(s).[/]");
        return 0;
    }
}
