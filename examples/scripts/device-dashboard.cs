#:property TargetFramework=net10.0
#:package Periphery@1.0.0-alpha.*
#:package Spectre.Console@0.54.0
#:property PublishAot=false

// ┌──────────────────────────────────────────────────────────────────────────┐
// │  device-dashboard.cs — Periphery + Spectre.Console                      │
// │  Run:  dotnet run device-dashboard.cs                                   │
// │                                                                          │
// │  A live terminal dashboard with three panels:                           │
// │    · Category bar chart   (top-left)                                    │
// │    · Real-time event log  (top-right)                                   │
// │    · Full connected-device table  (bottom)                              │
// │                                                                          │
// │  Press Ctrl+C to exit.                                                  │
// └──────────────────────────────────────────────────────────────────────────┘

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Periphery;
using Spectre.Console;
using Spectre.Console.Rendering;

// ── Shared state ───────────────────────────────────────────────────────────

var devices = new ConcurrentDictionary<string, DeviceInfo>();
var eventLog = new ConcurrentQueue<(DateTimeOffset At, bool Connected, DeviceInfo Device)>();
var startedAt = DateTimeOffset.Now;
var cts = new CancellationTokenSource();
const int MaxLog = 14;

Console.Title = "Periphery Device Dashboard";
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// ── Category metadata — label + 24-bit hex colour ─────────────────────────

static (string Label, string Hex) Meta(DeviceCategory cat) =>
    cat switch
    {
        DeviceCategory.Usb => ("USB", "#00ff87"),
        DeviceCategory.Bluetooth => ("Bluetooth", "#00afff"),
        DeviceCategory.Network => ("Network", "#00d75f"),
        DeviceCategory.Display => ("Display", "#ffd700"),
        DeviceCategory.Monitor => ("Monitor", "#ffaf00"),
        DeviceCategory.Hid => ("HID", "#ff5faf"),
        DeviceCategory.Keyboard => ("Keyboard", "#af87ff"),
        DeviceCategory.Mouse => ("Mouse", "#ff87af"),
        DeviceCategory.Audio => ("Audio", "#5fd7ff"),
        DeviceCategory.Storage => ("Storage", "#ffaf5f"),
        DeviceCategory.Camera => ("Camera", "#ff5fd7"),
        DeviceCategory.Battery => ("Battery", "#87ff00"),
        DeviceCategory.Ports => ("Ports", "#d7875f"),
        _ => ("Other", "#808080"),
    };

static Color HexColor(string hex)
{
    var h = hex.TrimStart('#');
    return new Color(
        Convert.ToByte(h[0..2], 16),
        Convert.ToByte(h[2..4], 16),
        Convert.ToByte(h[4..6], 16)
    );
}

// ── Per-device summary string for the "Info" column ───────────────────────

static string DeviceDetail(DeviceInfo d)
{
    if (
        d.Category is DeviceCategory.Network or DeviceCategory.Bluetooth
        && d.MacAddress is not null
    )
        return d.MacAddress.ToString();
    if (d.Category == DeviceCategory.Network && d.IPAddresses is { Length: > 0 })
        return d.IPAddresses.Value[0].ToString();
    if (
        d.Category is DeviceCategory.Display or DeviceCategory.Monitor
        && d.DisplayResolution is { } res
    )
        return $"{res.Width}×{res.Height}";
    if (d.Category == DeviceCategory.Storage && d.DriveType is { } dt)
        return dt.ToString();
    if (d.VendorId is not null && d.ProductId is not null)
        return $"{d.VendorId}:{d.ProductId}";
    if (d.SerialNumber is not null)
        return d.SerialNumber;
    if (d.DriverVersion is not null)
        return $"v{d.DriverVersion}";
    return string.Empty;
}

// ── Renderable builders ────────────────────────────────────────────────────

static IRenderable BuildHeader(int count, DateTimeOffset start)
{
    var uptime = DateTimeOffset.Now - start;
    var uptimeStr =
        uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}"
            : $"{uptime.Minutes:D2}:{uptime.Seconds:D2}";

    var grid = new Grid().Expand();
    grid.AddColumn();
    grid.AddColumn(new GridColumn().NoWrap().RightAligned());
    grid.AddRow(
        new Markup(
            "[bold #00afff] PERIPHERY[/] [bold white]DEVICE DASHBOARD[/]  [grey dim]live hardware monitor[/]"
        ),
        new Markup(
            $"[grey]devices[/] [bold #00ff87]{count}[/]   [grey]uptime[/] [white]{uptimeStr}[/]   [grey dim]Ctrl+C to exit[/]"
        )
    );

    return new Panel(grid).Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Padding(1, 0);
}

static IRenderable BuildCategoryPanel(IReadOnlyList<DeviceInfo> snapshot)
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
        var (label, hex) = Meta(cat);
        chart.AddItem(label, n, HexColor(hex));
    }

    return new Panel(chart)
        .Header("[bold #00afff]  Categories [/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey35)
        .Padding(1, 0);
}

static IRenderable BuildEventLog((DateTimeOffset At, bool Connected, DeviceInfo Device)[] events)
{
    var rows = new List<IRenderable>();

    foreach (var (at, connected, device) in events.Reverse())
    {
        var (label, hex) = Meta(device.Category);
        var arrow = connected ? "[bold green]▲[/]" : "[bold red]▼[/]";
        var name = Markup.Escape(device.Name ?? device.Id);
        rows.Add(
            new Markup(
                $"[grey]{at:HH:mm:ss}[/] {arrow} [white]{name}[/] [dim][{hex}]({label})[/][/]"
            )
        );
    }

    if (rows.Count == 0)
        rows.Add(new Markup("[grey italic]No events yet — plug or unplug a device.[/]"));

    return new Panel(new Rows(rows))
        .Header("[bold #00afff]  Recent Events [/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey35)
        .Padding(1, 0);
}

static IRenderable BuildDeviceTable(IReadOnlyList<DeviceInfo> snapshot)
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
        var (label, hex) = Meta(d.Category);
        var name = Markup.Escape(d.Name ?? d.Id);
        var mfr = Markup.Escape(d.Manufacturer ?? string.Empty);
        var detail = Markup.Escape(DeviceDetail(d));

        table.AddRow(
            $"[bold {hex}]{label}[/]",
            $"[white]{name}[/]",
            $"[grey]{mfr}[/]",
            $"[dim]{detail}[/]"
        );
    }

    return table;
}

// ── Watcher setup ──────────────────────────────────────────────────────────

await using var watcher = Devices.Watch();

watcher.Activated += (_, e) =>
{
    devices[e.Device.Id] = e.Device;
    eventLog.Enqueue((DateTimeOffset.Now, true, e.Device));
    while (eventLog.Count > MaxLog * 2)
        eventLog.TryDequeue(out var droppedC);
};

watcher.Deactivated += (_, e) =>
{
    devices.TryRemove(e.Device.Id, out var removedDevice);
    eventLog.Enqueue((DateTimeOffset.Now, false, e.Device));
    while (eventLog.Count > MaxLog * 2)
        eventLog.TryDequeue(out var droppedD);
};

// ── Boot: enumerate devices under a spinner ────────────────────────────────

await AnsiConsole
    .Status()
    .Spinner(Spinner.Known.Dots2)
    .SpinnerStyle(Style.Parse("#00afff"))
    .StartAsync(
        "[#00afff]Enumerating devices...[/]",
        async ctx =>
        {
            await watcher.StartAsync(cts.Token);
            ctx.Status($"[#00ff87]Found {devices.Count} device(s). Starting dashboard...[/]");
            await Task.Delay(TimeSpan.FromMilliseconds(700));
        }
    );

// ── Guard: Live display requires an interactive terminal ───────────────────

if (Console.IsOutputRedirected || Console.IsErrorRedirected)
{
    AnsiConsole.MarkupLine(
        "[red]Error:[/] device-dashboard requires an interactive terminal (stdout must not be redirected)."
    );
    return;
}

// ── Live layout ────────────────────────────────────────────────────────────

var layout = new Layout("Root").SplitRows(
    new Layout("Header").Size(5),
    new Layout("Middle").SplitColumns(
        new Layout("Categories").Ratio(1),
        new Layout("Events").Ratio(1)
    ),
    new Layout("Devices").Ratio(2)
);

void RefreshLayout()
{
    var snap = devices.Values.ToList();
    var events = eventLog.ToArray().TakeLast(MaxLog).ToArray();

    layout["Header"].Update(BuildHeader(snap.Count, startedAt));
    layout["Categories"].Update(BuildCategoryPanel(snap));
    layout["Events"].Update(BuildEventLog(events));
    layout["Devices"].Update(BuildDeviceTable(snap));
}

RefreshLayout();

await AnsiConsole
    .Live(layout)
    .AutoClear(false)
    .Overflow(VerticalOverflow.Ellipsis)
    .Cropping(VerticalOverflowCropping.Bottom)
    .StartAsync(async ctx =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            RefreshLayout();
            ctx.Refresh();
            try
            {
                await Task.Delay(1000, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    });

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]Dashboard stopped. Goodbye! 👋[/]");
