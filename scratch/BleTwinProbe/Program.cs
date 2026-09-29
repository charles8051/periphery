using System.Diagnostics;
using System.Text.RegularExpressions;
using Periphery;

// ─────────────────────────────────────────────────────────────────────────────
// Two paired LE units that share a name, for ADR-0083 NEG-005 (bench step 4).
//
//   dotnet run --project scratch/BleTwinProbe <name> [seconds] [--stop-file <path>]
//
//   name         the units' shared device name (for example "Periphery Bench Twin")
//   seconds      how long to watch (default 120)
//   --stop-file  also stop as soon as this file exists
//
// Prints what differs between the units' BTHLE\DEV_ link nodes, then logs three trackers
// keyed on what they share while their links come and go:
//   by-name   DeviceTracker, OfCategory(Bluetooth).WithName(name)
//   by-pnp    DeviceTracker, WithUsbId(the units' VendorId, ProductId), when they carry one
//   multi     MultiDeviceTracker, OfCategory(Bluetooth).WithName(name)
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: BleTwinProbe <name> [seconds] [--stop-file <path>]");
    return 2;
}

string name = args[0];
int seconds = args.Skip(1).Select(a => int.TryParse(a, out var v) ? v : 0).FirstOrDefault(v => v > 0, 120);
int stopArg = Array.IndexOf(args, "--stop-file");
string? stopFile = stopArg >= 0 && stopArg + 1 < args.Length ? args[stopArg + 1] : null;
var clock = Stopwatch.StartNew();
void Log(string source, string message) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s  {source,-9}  {message}");

static bool IsLeLinkNode(DeviceInfo d) =>
    BluetoothAddress.TryParseInstanceId(d.Id.Value, out _, out var t) && t == BluetoothTransport.LowEnergy;

var all = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
var twins = all.Where(d => IsLeLinkNode(d) && d.Name == name).ToList();
var visible = twins.Select(d => { BluetoothAddress.TryParseInstanceId(d.Id.Value, out var a, out _); return a.Value; }).ToHashSet();
Log("probe", $"{twins.Count} LE link node(s) named '{name}'");
if (twins.Count != 2)
    return 1;

foreach (var d in twins)
    Log("twin", $"{Mask(d.Id.Value)}  container {d.ContainerId}  VID {d.VendorId?.ToString() ?? "-"} PID {d.ProductId?.ToString() ?? "-"}  serial {d.SerialNumber ?? "-"}");

// Every field and property the two nodes carry, compared.
var fields = new (string Name, Func<DeviceInfo, object?> Get)[]
{
    ("Id", d => d.Id), ("Name", d => d.Name), ("Manufacturer", d => d.Manufacturer), ("ClassGuid", d => d.ClassGuid),
    ("ContainerId", d => d.ContainerId), ("VendorId", d => d.VendorId), ("ProductId", d => d.ProductId),
    ("SerialNumber", d => d.SerialNumber), ("BusType", d => d.BusType), ("LocationPath", d => d.LocationPath),
    ("ParentId", d => d.ParentId), ("Driver", d => d.Driver), ("BatteryChargePercent", d => d.BatteryChargePercent),
    ("Tags", d => string.Join(",", d.Tags.Order())),
};
foreach (var (field, get) in fields)
{
    string a = Show(get(twins[0])), b = Show(get(twins[1]));
    Log("compare", $"{field,-22} {(a == b ? "same" : "DIFFERS")}  {a}{(a == b ? "" : $" | {b}")}");
}
foreach (var key in twins[0].Properties.Keys.Union(twins[1].Properties.Keys).Order())
{
    string a = Show(twins[0].Properties.GetValueOrDefault(key)), b = Show(twins[1].Properties.GetValueOrDefault(key));
    Log("compare", $"prop {key,-17} {(a == b ? "same" : "DIFFERS")}  {a}{(a == b ? "" : $" | {b}")}");
}

var byName = new DeviceTracker(f => f.OfCategory(DeviceCategory.Bluetooth).WithName(name), "by-name");
var trackers = new List<DeviceTracker> { byName };
if (twins[0].VendorId is HardwareId vid && twins[0].ProductId is HardwareId pid)
    trackers.Add(new DeviceTracker(f => f.WithUsbId(vid, pid), "by-pnp"));
foreach (var tracker in trackers)
{
    string label = tracker.Name!;
    tracker.StateChanged += (_, s) => Log(label, $"{s.ActivityStatus,-8} {(s.Device is null ? "no device" : Describe(s.Device))}");
}

await using (var watcher = Devices.Watch().OfCategory(DeviceCategory.Bluetooth).AddTrackers([.. trackers]))
{
    var multi = watcher.AddMultiTracker(f => f.OfCategory(DeviceCategory.Bluetooth).WithName(name), "multi");
    multi.DeviceAdded += (_, _) => Log("multi", $"child tracker added, {multi.Count} in all");
    using var multiSubscription = multi.Subscribe(new Observer(s =>
        Log("multi", $"{s.ActivityStatus,-8} {(s.Device is null ? "no device" : Describe(s.Device))}")));

    watcher.Activated += (_, e) => Edge("Activated", e.Device);
    watcher.Deactivated += (_, e) => Edge("Deactivated", e.Device);
    watcher.Appeared += (_, e) => Edge("Appeared", e.Device);
    watcher.Disappeared += (_, e) => Edge("Disappeared", e.Device);
    await watcher.StartAsync();
    Log("probe", $"watching {seconds} s");

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    try
    {
        while (!(stopFile is not null && File.Exists(stopFile)))
            await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
    }
    catch (OperationCanceledException) { }

    foreach (var tracker in trackers)
        Log("final", $"{tracker.Name,-8} {tracker.CurrentState.ActivityStatus,-8} " +
                     $"{(tracker.CurrentState.Device is null ? "no device" : Describe(tracker.CurrentState.Device))}");
    Log("final", $"multi    {multi.Count} child tracker(s)");
}
return 0;

void Edge(string kind, DeviceInfo device)
{
    if (twins.Any(t => t.Id == device.Id))
        Log("watcher", $"{kind,-11} {Describe(device)}");
}

string Describe(DeviceInfo d) => $"{Mask(d.Id.Value)}  IsActive={d.IsActive}";

string Show(object? value) => value switch
{
    null => "-",
    string s => Mask(s),
    System.Collections.IEnumerable e and not string => Mask(string.Join(";", e.Cast<object?>().Select(x => x?.ToString()))),
    _ => Mask(value.ToString() ?? "-"),
};

string Mask(string text) =>
    Regex.Replace(text, "(?i)[0-9a-f]{12}", m =>
        BluetoothAddress.TryParse(m.Value, out var a) && visible.Contains(a.Value) ? m.Value : "<addr12>");

sealed class Observer(Action<DeviceTrackerState> onNext) : IObserver<DeviceTrackerState>
{
    public void OnNext(DeviceTrackerState value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
