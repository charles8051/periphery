// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.Reflection;
using Periphery.Cli.Diagnostics;
using Periphery.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

internal sealed class WatchCommand : AsyncCommand<WatchCommand.Settings>
{
    public sealed class Settings : DiagnosticSettings
    {
        [Description("Restrict to a single device category (Usb, Bluetooth, Camera, …).")]
        [CommandOption("-c|--category <CATEGORY>")]
        public DeviceCategory? Category { get; init; }

        [Description("Filter by USB vendor ID (4-hex-digit, e.g. 046D).")]
        [CommandOption("--vid <VID>")]
        public string? Vid { get; init; }

        [Description("Filter by USB product ID (4-hex-digit, e.g. C52B). Requires --vid.")]
        [CommandOption("--pid <PID>")]
        public string? Pid { get; init; }

        [Description("Filter by device name substring (case-insensitive).")]
        [CommandOption("--name <PATTERN>")]
        public string? Name { get; init; }

        [Description("Filter by manufacturer substring (case-insensitive).")]
        [CommandOption("--manufacturer <PATTERN>")]
        public string? Manufacturer { get; init; }

        [Description("Filter by serial number (exact match).")]
        [CommandOption("--serial <SERIAL>")]
        public string? Serial { get; init; }

        [Description("Filter by bus type (Usb, Pci, Bluetooth, Network, …).")]
        [CommandOption("--bus <BUS>")]
        public BusType? Bus { get; init; }

        [Description("Comma-separated property names to stream change events for (e.g. BatteryChargePercent,BatteryStatus). Default: no property events shown.")]
        [CommandOption("--properties <NAMES>")]
        public string? Properties { get; init; }

        public override ValidationResult Validate()
        {
            if (Pid is not null && Vid is null)
                return ValidationResult.Error("--pid requires --vid.");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        settings.ApplyLogging();   // --verbose: route Periphery's CM-notification / reset / recovery logs here

        var watchedProperties = ParseProperties(settings.Properties);
        var filterSummary = DescribeFilter(settings);

        AnsiConsole.MarkupLine($"[grey]Watching [/][bold]{Markup.Escape(filterSummary)}[/][grey] — Ctrl+C to stop.[/]");
        if (settings.Verbose)
            AnsiConsole.MarkupLine("[grey]Verbose: streaming tree-presence (△/▽) and OS plumbing under the hood.[/]");
        if (watchedProperties.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Property events:[/] [bold]{Markup.Escape(string.Join(", ", watchedProperties))}[/]");
        }
        AnsiConsole.WriteLine();

        await using var watcher = Devices.Watch();
        ApplyFilter(watcher, settings);

        watcher.Activated += (_, e) => PrintConnectEvent(connected: true, e.Device);
        watcher.Deactivated += (_, e) => PrintConnectEvent(connected: false, e.Device);

        if (settings.Verbose)
        {
            // Tree-presence lifecycle, distinct from active/inactive: a re-enumerating reset
            // shows here as ▽ then △; a soft disable/enable may show neither (the OS plumbing
            // still surfaces via the --verbose log routing above).
            watcher.Appeared += (_, e) => PrintPresenceEvent(present: true, e.Device);
            watcher.Disappeared += (_, e) => PrintPresenceEvent(present: false, e.Device);
        }

        if (watchedProperties.Count > 0)
        {
            watcher.PropertyChanged += (_, e) =>
            {
                var matched = e.ChangedProperties.Intersect(watchedProperties, StringComparer.Ordinal).ToList();
                if (matched.Count == 0) return;
                PrintPropertyEvent(e.Current, e.Previous, matched);
            };
        }

        await watcher.StartAsync(cancellationToken);
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { }

        AnsiConsole.MarkupLine("[grey]Stopped.[/]");
        return 0;
    }

    // ── Filter construction ────────────────────────────────────────────

    private static void ApplyFilter(DeviceWatcher watcher, Settings s)
    {
        if (s.Category is { } cat && cat != DeviceCategory.All)
            watcher.OfCategory(cat);
        if (s.Vid is not null)
            watcher.WithUsbId(s.Vid, s.Pid);
        if (s.Name is not null)
            watcher.WithName(s.Name);
        if (s.Manufacturer is not null)
            watcher.ByManufacturer(s.Manufacturer);
        if (s.Serial is not null)
            watcher.WithSerialNumber(s.Serial);
        if (s.Bus is not null)
            watcher.WithBusType(s.Bus.Value);
    }

    private static string DescribeFilter(Settings s)
    {
        var parts = new List<string>();
        if (s.Category is { } cat && cat != DeviceCategory.All) parts.Add($"category={cat}");
        if (s.Vid is not null) parts.Add(s.Pid is not null ? $"usb={s.Vid}:{s.Pid}" : $"vid={s.Vid}");
        if (s.Name is not null) parts.Add($"name~{s.Name}");
        if (s.Manufacturer is not null) parts.Add($"manufacturer~{s.Manufacturer}");
        if (s.Serial is not null) parts.Add($"serial={s.Serial}");
        if (s.Bus is not null) parts.Add($"bus={s.Bus}");
        return parts.Count == 0 ? "all devices" : string.Join(" ", parts);
    }

    // ── Properties parsing ─────────────────────────────────────────────

    private static IReadOnlySet<string> ParseProperties(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new HashSet<string>(StringComparer.Ordinal);
        return new HashSet<string>(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
    }

    // ── Output ─────────────────────────────────────────────────────────

    private static void PrintConnectEvent(bool connected, DeviceInfo device)
    {
        var (label, hex) = CategoryMeta.Get(device.Category);
        var arrow = connected ? "[bold green]▲[/]" : "[bold red]▼[/]";
        var name = Markup.Escape(device.Name ?? device.Id);
        AnsiConsole.MarkupLine(
            $"[grey]{DateTimeOffset.Now:HH:mm:ss}[/] {arrow} [white]{name}[/] [dim][{hex}]({label})[/][/]");
    }

    // Tree-presence (appeared/disappeared) — hollow triangles, to read distinctly from the
    // filled ▲/▼ active/inactive markers. Only shown in --verbose.
    private static void PrintPresenceEvent(bool present, DeviceInfo device)
    {
        var (label, hex) = CategoryMeta.Get(device.Category);
        var glyph = present ? "[green]△[/]" : "[red]▽[/]";
        var name = Markup.Escape(device.Name ?? device.Id);
        AnsiConsole.MarkupLine(
            $"[grey]{DateTimeOffset.Now:HH:mm:ss}[/] {glyph} [dim]{name} [{hex}]({label})[/] (tree)[/]");
    }

    private static void PrintPropertyEvent(DeviceInfo current, DeviceInfo previous, IReadOnlyList<string> changed)
    {
        var (label, hex) = CategoryMeta.Get(current.Category);
        var name = Markup.Escape(current.Name ?? current.Id);
        var deltas = string.Join(", ", changed.Select(prop =>
            $"{Markup.Escape(prop)}: [grey]{Markup.Escape(ReadProperty(previous, prop))}[/]→[white]{Markup.Escape(ReadProperty(current, prop))}[/]"));
        AnsiConsole.MarkupLine(
            $"[grey]{DateTimeOffset.Now:HH:mm:ss}[/] [bold yellow]⚙[/] [white]{name}[/] [dim][{hex}]({label})[/][/]  {deltas}");
    }

    private static string ReadProperty(DeviceInfo info, string propertyName)
    {
        var prop = typeof(DeviceInfo).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return "?";
        var value = prop.GetValue(info);
        return value?.ToString() ?? "(null)";
    }
}
