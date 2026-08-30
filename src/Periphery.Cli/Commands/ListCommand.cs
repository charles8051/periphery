// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Periphery.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

internal sealed class ListCommand : AsyncCommand<ListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Filter to a single device category (Usb, Bluetooth, Camera, …).")]
        [CommandOption("-c|--category <CATEGORY>")]
        public DeviceCategory? Category { get; init; }

        [Description("Emit raw JSON instead of a formatted table.")]
        [CommandOption("--json")]
        public bool Json { get; init; }

        [Description("Dump every populated property per device as a tree (instead of the compact table).")]
        [CommandOption("-v|--verbose")]
        public bool Verbose { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var query = Devices.Enumerate();
        if (settings.Category is { } cat && cat != DeviceCategory.All)
            query = query.OfCategory(cat);

        var devices = await query.ToListAsync(cancellationToken);

        if (settings.Json)
        {
            // UnsafeRelaxedJsonEscaping: serialize & < > literally so device instance IDs like
            // USB\VID_10C4&PID_8A7E\... copy-paste cleanly. Output is console text, not HTML,
            // so relaxed escaping is safe here.
            var opts = new JsonSerializerOptions(DeviceInfoJsonContext.Default.Options)
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var ctx = new DeviceInfoJsonContext(opts);
            using var stdout = Console.OpenStandardOutput();
            await JsonSerializer.SerializeAsync(stdout, devices, ctx.GetTypeInfo(typeof(List<DeviceInfo>))!, cancellationToken);
            return 0;
        }

        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey italic]No devices found.[/]");
            return 0;
        }

        var ordered = devices
            .Where(d => d.Category != DeviceCategory.All)
            .OrderBy(d => d.Category.ToString())
            .ThenBy(d => d.Name ?? d.Id)
            .ToList();

        if (settings.Verbose)
        {
            RenderVerbose(ordered);
        }
        else
        {
            RenderCompactTable(ordered);
        }

        AnsiConsole.MarkupLine($"[grey]{devices.Count} device(s).[/]");
        return 0;
    }

    private static void RenderVerbose(IReadOnlyList<DeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            var (label, hex) = CategoryMeta.Get(d.Category);
            // Tree header — bold device name with a colored category
            // chip; full identity is exposed in the Id property below
            // alongside everything else.
            var header =
                $"[bold {hex}]{label}[/]  [bold white]{Markup.Escape(d.Name ?? d.Id)}[/]";

            AnsiConsole.Write(DeviceInfoTreeBuilder.Build(d, header));
            AnsiConsole.WriteLine();
        }
    }

    private static void RenderCompactTable(IReadOnlyList<DeviceInfo> devices)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .AddColumn(new TableColumn("[bold grey]Category[/]").Width(12))
            .AddColumn(new TableColumn("[bold grey]Name[/]"))
            .AddColumn(new TableColumn("[bold grey]Manufacturer[/]").Width(22))
            .AddColumn(new TableColumn("[bold grey]Info[/]").Width(28));

        foreach (var d in devices)
        {
            var (label, hex) = CategoryMeta.Get(d.Category);
            table.AddRow(
                $"[bold {hex}]{label}[/]",
                $"[white]{Markup.Escape(d.Name ?? d.Id)}[/]",
                $"[grey]{Markup.Escape(d.Manufacturer ?? string.Empty)}[/]",
                $"[dim]{Markup.Escape(CategoryMeta.Detail(d))}[/]");
        }

        AnsiConsole.Write(table);
    }
}
