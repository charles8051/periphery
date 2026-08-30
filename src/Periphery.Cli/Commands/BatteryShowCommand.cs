// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Runtime.Versioning;
using Periphery.Hid.Codecs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery battery show &lt;device-id&gt;</c> — runs the ADR-0026
/// two-piece classification + snapshot for one device. Classification
/// is already applied during core enumeration via the registered
/// <see cref="HidBatteryEnricher"/>; this command runs
/// <see cref="HidBattery.ReadSnapshotAsync"/> (Option D static helper)
/// for the live data and dumps both layers. Verifies a specific UPS's
/// codec response before integrating it into a polling loop.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
internal sealed class BatteryShowCommand : AsyncCommand<BatteryShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Device ID (from `devices list -v`). Quote if it contains backslashes.")]
        [CommandArgument(0, "<DEVICE_ID>")]
        public string DeviceId { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // WithId, not a hand-rolled Ordinal compare — see issue #231: instance ids
        // are case-insensitive by contract and re-enumerate in different casing.
        var matches = await Devices.Enumerate()
            .WithId(settings.DeviceId)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No device found with Id '{Markup.Escape(settings.DeviceId)}'. "
                + "Run `periphery devices list -v` to copy the exact Id.[/]");
            return 1;
        }
        var device = matches[0];

        AnsiConsole.MarkupLine(
            $"[grey]Device:[/] [white]{Markup.Escape(device.Name ?? device.Id)}[/] " +
            $"[grey]Category=[/][white]{device.Category}[/] " +
            $"[grey]{device.VendorId}:{device.ProductId}[/]");

        // Classification is already applied by core enumeration via
        // the registered HidBatteryEnricher (ADR-0024 §3c). The Tag is
        // either present from that pass or absent because no codec is
        // registered for the device's (VID, PID).
        var tagged = device;
        bool hasBatteryTag = tagged.Tags.Contains(DeviceTags.Battery);
        bool hasBatteryCategory = tagged.Category == DeviceCategory.Battery;

        if (!hasBatteryTag && !hasBatteryCategory)
        {
            AnsiConsole.MarkupLine("[yellow]Not classified as a battery device. " +
                "Reasons: not HID-category, missing VID/PID, no codec registered in HidQuirks for the (VID, PID), " +
                "and not an OS-classified Battery either.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]Classified as battery.[/] " +
            $"[grey]Tag={(hasBatteryTag ? "yes" : "no")} " +
            $"Category={(hasBatteryCategory ? "Battery" : tagged.Category.ToString())}[/]");

        // Step 2: live snapshot read (Option D — explicit I/O cost).
        // Only applies to HID UPSs; system batteries already have their
        // fields populated on DeviceInfo by core's WindowsBatteryEnricher.
        if (tagged.Category != DeviceCategory.Hid)
        {
            AnsiConsole.MarkupLine("[grey]System battery — DeviceInfo fields populated by core enricher:[/]");
            DumpFields(tagged.BatteryChargePercent, tagged.BatteryStatus,
                tagged.IsExternalPowerConnected, tagged.IsBatteryLow);
            return 0;
        }

        AnsiConsole.MarkupLine("[grey]Running HidBattery.ReadSnapshotAsync (opens transient HID handle)…[/]");
        HidBatterySnapshot? snapshot;
        try
        {
            snapshot = await HidBattery.ReadSnapshotAsync(tagged, cancellationToken);
        }
        catch (HidException ex)
        {
            AnsiConsole.MarkupLine($"[red]Snapshot read failed: {Markup.Escape(ex.Message)}[/]");
            if (ex.InnerException is not null)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(ex.InnerException.Message)}[/]");
            return 1;
        }

        if (snapshot is null)
        {
            AnsiConsole.MarkupLine("[yellow]ReadSnapshotAsync returned null — no codec registered " +
                "for this device's (VID, PID). HidBatteryEnricher should have agreed; check for state drift.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]Codec read successful.[/]");
        DumpFields(snapshot.Value.BatteryChargePercent, snapshot.Value.BatteryStatus,
            snapshot.Value.IsExternalPowerConnected, snapshot.Value.IsBatteryLow);
        return 0;
    }

    private static void DumpFields(int? charge, BatteryStatus? status, bool? ac, bool? low)
    {
        AnsiConsole.MarkupLine($"  [grey]BatteryChargePercent:[/]    [white]{charge?.ToString() ?? "—"}[/]");
        AnsiConsole.MarkupLine($"  [grey]BatteryStatus:[/]           [white]{status?.ToString() ?? "—"}[/]");
        AnsiConsole.MarkupLine($"  [grey]IsExternalPowerConnected:[/] [white]{ac?.ToString().ToLowerInvariant() ?? "—"}[/]");
        AnsiConsole.MarkupLine($"  [grey]IsBatteryLow:[/]             [white]{low?.ToString().ToLowerInvariant() ?? "—"}[/]");
    }
}
