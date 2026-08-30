// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using Periphery.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Periphery.Cli.Commands;

internal sealed class DashboardCommand : AsyncCommand
{
    private const int MaxLog = 14;

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] dashboard requires an interactive terminal (stdout/stderr cannot be redirected).");
            return 1;
        }

        var eventLog = new ConcurrentQueue<(DateTimeOffset At, bool Connected, DeviceInfo Device)>();
        var startedAt = DateTimeOffset.Now;

        Console.Title = "Periphery Device Dashboard";

        await using var watcher = Devices.Watch();
        var multiTracker = watcher.AddMultiTracker(_ => { }, name: "AllDevices");

        multiTracker.DeviceAdded += (_, tracker) =>
        {
            tracker.Activated += (_, transition) => Log(connected: true, transition.After.Device!);
            tracker.Deactivated += (_, transition) => Log(connected: false, transition.Before.Device!);
        };

        void Log(bool connected, DeviceInfo device)
        {
            eventLog.Enqueue((DateTimeOffset.Now, connected, device));
            while (eventLog.Count > MaxLog * 2)
                eventLog.TryDequeue(out var _drop);
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Parse("#00afff"))
            .StartAsync("[#00afff]Enumerating devices...[/]", async ctx =>
            {
                await watcher.StartAsync(cancellationToken);
                var active = multiTracker.Trackers.Values.Count(t => t.IsActive);
                ctx.Status($"[#00ff87]Found {active} device(s). Starting dashboard...[/]");
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            });

        var layout = new Layout("Root").SplitRows(
            new Layout("Header").Size(5),
            new Layout("Middle").SplitColumns(
                new Layout("Categories").Ratio(1),
                new Layout("Events").Ratio(1)),
            new Layout("Devices").Ratio(2));

        void Refresh()
        {
            var snap = multiTracker.Trackers.Values
                .Where(t => t.IsActive && t.Device is not null)
                .Select(t => t.Device!)
                .ToList();
            var events = eventLog.ToArray().TakeLast(MaxLog).ToArray();

            layout["Header"].Update(BuildHeader(snap.Count, startedAt));
            layout["Categories"].Update(BuildCategoryPanel(snap));
            layout["Events"].Update(BuildEventLog(events));
            layout["Devices"].Update(BuildDeviceTable(snap));
        }

        Refresh();

        await AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async ctx =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Refresh();
                    ctx.Refresh();
                    try { await Task.Delay(1000, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Dashboard stopped.[/]");
        return 0;
    }

    private static IRenderable BuildHeader(int count, DateTimeOffset start)
    {
        var uptime = DateTimeOffset.Now - start;
        var uptimeStr = uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}"
            : $"{uptime.Minutes:D2}:{uptime.Seconds:D2}";

        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn(new GridColumn().NoWrap().RightAligned());
        grid.AddRow(
            new Markup("[bold #00afff] PERIPHERY[/] [bold white]DEVICE DASHBOARD[/]  [grey dim]live hardware monitor[/]"),
            new Markup($"[grey]devices[/] [bold #00ff87]{count}[/]   [grey]uptime[/] [white]{uptimeStr}[/]   [grey dim]Ctrl+C to exit[/]"));

        return new Panel(grid).Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Padding(1, 0);
    }

    private static IRenderable BuildCategoryPanel(IReadOnlyList<DeviceInfo> snapshot)
    {
        var counts = snapshot
            .Where(d => d.Category != DeviceCategory.All)
            .GroupBy(d => d.Category)
            .Select(g => (Category: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(12)
            .ToList();

        if (counts.Count == 0)
        {
            return new Panel(new Markup("[grey italic]Waiting for devices...[/]"))
                .Header("[bold #00afff]  Categories [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey35)
                .Padding(1, 0);
        }

        var chart = new BarChart().Width(46);
        foreach (var (cat, n) in counts)
        {
            var (label, hex) = CategoryMeta.Get(cat);
            chart.AddItem(label, n, CategoryMeta.HexColor(hex));
        }

        return new Panel(chart)
            .Header("[bold #00afff]  Categories [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Padding(1, 0);
    }

    private static IRenderable BuildEventLog((DateTimeOffset At, bool Connected, DeviceInfo Device)[] events)
    {
        var rows = new List<IRenderable>();

        foreach (var (at, connected, device) in events.Reverse())
        {
            var (label, hex) = CategoryMeta.Get(device.Category);
            var arrow = connected ? "[bold green]▲[/]" : "[bold red]▼[/]";
            var name = Markup.Escape(device.Name ?? device.Id);
            rows.Add(new Markup($"[grey]{at:HH:mm:ss}[/] {arrow} [white]{name}[/] [dim][{hex}]({label})[/][/]"));
        }

        if (rows.Count == 0)
            rows.Add(new Markup("[grey italic]No events yet — plug or unplug a device.[/]"));

        return new Panel(new Rows(rows))
            .Header("[bold #00afff]  Recent Events [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Padding(1, 0);
    }

    private static IRenderable BuildDeviceTable(IReadOnlyList<DeviceInfo> snapshot)
    {
        var table = new Table()
            .Expand()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title("[bold #00afff]  Connected Devices  [/]")
            .AddColumn(new TableColumn("[bold grey]Category[/]").Width(12))
            .AddColumn(new TableColumn("[bold grey]Name[/]"))
            .AddColumn(new TableColumn("[bold grey]Manufacturer[/]").Width(20))
            .AddColumn(new TableColumn("[bold grey]Info[/]").Width(24));

        var sorted = snapshot
            .Where(d => d.Category != DeviceCategory.All)
            .OrderBy(d => d.Category.ToString())
            .ThenBy(d => d.Name ?? d.Id)
            .Take(20)
            .ToList();

        if (sorted.Count == 0)
        {
            table.AddRow("[grey]—[/]", "[grey]Waiting for devices...[/]", "[grey]—[/]", "[grey]—[/]");
            return table;
        }

        foreach (var d in sorted)
        {
            var (label, hex) = CategoryMeta.Get(d.Category);
            table.AddRow(
                $"[bold {hex}]{label}[/]",
                $"[white]{Markup.Escape(d.Name ?? d.Id)}[/]",
                $"[grey]{Markup.Escape(d.Manufacturer ?? string.Empty)}[/]",
                $"[dim]{Markup.Escape(CategoryMeta.Detail(d))}[/]");
        }

        return table;
    }
}
