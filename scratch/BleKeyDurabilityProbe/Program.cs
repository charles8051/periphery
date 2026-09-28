using System.Diagnostics;
using System.Text.RegularExpressions;
using Periphery;

// ─────────────────────────────────────────────────────────────────────────────
// Watches one LE peripheral's link node through a transition, for issue #232.
//
//   dotnet run --project scratch/BleKeyDurabilityProbe <address> [seconds] [--stop-file <path>]
//                                                     [--replacement <address>]
//
//   address        the peripheral's address as the bench firmware logs it (AA:BB:CC:DD:EE:FF)
//   seconds        how long to watch (default 120; Ctrl+C stops early)
//   --stop-file    also stop as soon as this file exists, for a transition a person performs.
//                  If the file holds an address, it is used as --replacement, since the address
//                  a re-paired peripheral comes back on is known only after it pairs
//   --replacement  the address the peripheral logged when it came back, if the node is expected
//                  to change; the replacement is then the node carrying it
//
// Run it, perform the transition, and let it finish. It prints the link node before and
// after, and every state change of three trackers bound at start. If the node is gone
// afterwards, it reports a replacement: the node at --replacement, or, without it, the one new
// LE link node carrying the target's device name. A name match is only a candidate, since two
// units of one model share a name, and is reported as unverified.
//
//   by-id         DeviceFilter.WithId(the node's instance id at start)
//   by-container  DeviceFilter.WithContainerId(the node's container id at start)
//   by-address    BluetoothAddress.TryParseInstanceId == the address, LE transport
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length < 1 || !BluetoothAddress.TryParse(args[0], out var target))
{
    Console.Error.WriteLine("usage: BleKeyDurabilityProbe <address> [seconds]");
    return 2;
}

int seconds = args.Skip(1).Select(a => int.TryParse(a, out var v) ? v : 0).FirstOrDefault(v => v > 0, 120);
int stopArg = Array.IndexOf(args, "--stop-file");
string? stopFile = stopArg >= 0 && stopArg + 1 < args.Length ? args[stopArg + 1] : null;
int replacementArg = Array.IndexOf(args, "--replacement");
BluetoothAddress? expectedReplacement =
    replacementArg >= 0 && replacementArg + 1 < args.Length && BluetoothAddress.TryParse(args[replacementArg + 1], out var r)
        ? r
        : null;
var clock = Stopwatch.StartNew();
void Log(string source, string message) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s  {source,-12}  {message}");

bool IsTarget(DeviceInfo d) =>
    BluetoothAddress.TryParseInstanceId(d.Id.Value, out var a, out var t)
    && t == BluetoothTransport.LowEnergy
    && a == target;

var visible = new HashSet<ulong> { target.Value };
var (before, beforeNodes) = await SnapshotAsync("before");
if (before is null)
{
    Log("probe", $"no BTHLE\\DEV_ node for {target}; pair the peripheral first");
    return 1;
}

var trackers = new List<DeviceTracker>
{
    new(f => f.WithId(before.Id.Value), "by-id"),
    new(f => f.Where(IsTarget), "by-address"),
};
if (before.ContainerId is Guid container)
    trackers.Add(new(f => f.WithContainerId(container), "by-container"));

foreach (var tracker in trackers)
{
    string name = tracker.Name!;
    tracker.StateChanged += (_, s) =>
        Log(name, $"{s.ActivityStatus,-8} {(s.Device is null ? "no device" : Describe(s.Device))}");
}

await using (var watcher = Devices.Watch().OfCategory(DeviceCategory.Bluetooth).AddTrackers([.. trackers]))
{
    watcher.Appeared += (_, e) => Edge("Appeared", e.Device);
    watcher.Disappeared += (_, e) => Edge("Disappeared", e.Device);
    watcher.Activated += (_, e) => Edge("Activated", e.Device);
    watcher.Deactivated += (_, e) => Edge("Deactivated", e.Device);
    await watcher.StartAsync();
    Log("probe", $"watching {seconds} s; perform the transition now");

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    try
    {
        // A person performs the transition, so the end is a file they (or a script) create.
        while (!(stopFile is not null && File.Exists(stopFile)))
            await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
    }
    catch (OperationCanceledException) { }

    foreach (var tracker in trackers)
        Log("final", $"{tracker.Name,-12} {tracker.CurrentState.ActivityStatus,-8} " +
                     $"{(tracker.CurrentState.Device is null ? "no device" : Describe(tracker.CurrentState.Device))}");
}

if (expectedReplacement is null && stopFile is not null && File.Exists(stopFile)
    && BluetoothAddress.TryParse(File.ReadAllText(stopFile).Trim(), out var fromFile))
{
    expectedReplacement = fromFile;
}

var (after, afterNodes) = await SnapshotAsync("after");
if (after is not null)
{
    Log("result", $"instance id  {(after.Id == before.Id ? "same" : "changed")}");
    Log("result", $"container id {(after.ContainerId == before.ContainerId ? "same" : "changed")}");
    Log("result", "address      same (the node was found by it)");
    return 0;
}

var beforeIds = beforeNodes.Select(d => d.Id).ToHashSet();
List<DeviceInfo> candidates;
string basis;
if (expectedReplacement is BluetoothAddress expected)
{
    candidates = afterNodes.Where(d => BluetoothAddress.TryParseInstanceId(d.Id.Value, out var a, out var t)
                                       && t == BluetoothTransport.LowEnergy && a == expected).ToList();
    basis = "at the address the peripheral logged";
}
else
{
    candidates = afterNodes
        .Where(d => !beforeIds.Contains(d.Id) && string.Equals(d.Name, before.Name, StringComparison.Ordinal))
        .ToList();
    basis = "matched by device name only, unverified";
}

// Candidates stay masked unless exactly one is found: several same-name nodes are as likely
// to be other units of one model as the peripheral under test.
if (candidates.Count != 1)
{
    Log("result", $"node gone; {candidates.Count} replacement candidate(s) {basis}");
    return 0;
}

var replacement = candidates[0];
BluetoothAddress.TryParseInstanceId(replacement.Id.Value, out var replacementAddress, out _);
visible.Add(replacementAddress.Value);
Log("replacement", $"{replacementAddress}  {Describe(replacement)}  ({basis})");
Log("result", $"instance id  changed ({replacement.Id.Value})");
Log("result", $"container id {(replacement.ContainerId == before.ContainerId ? "same" : "changed")}");
Log("result", $"address      changed ({target} -> {replacementAddress})");
return 0;

async Task<(DeviceInfo? Target, List<DeviceInfo> LinkNodes)> SnapshotAsync(string label)
{
    var nodes = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
    var le = nodes.Where(d => BluetoothAddress.TryParseInstanceId(d.Id.Value, out _, out var t)
                              && t == BluetoothTransport.LowEnergy).ToList();
    foreach (var d in le)
        Log(label, Describe(d));
    var match = le.Where(IsTarget).ToList();
    if (match.Count > 1)
        Log(label, $"{match.Count} nodes carry {target}");
    return (match.FirstOrDefault(), le);
}

void Edge(string kind, DeviceInfo device)
{
    if (BluetoothAddress.TryParseInstanceId(device.Id.Value, out _, out var t) && t == BluetoothTransport.LowEnergy)
        Log("watcher", $"{kind,-11} {Describe(device)}");
}

string Describe(DeviceInfo d) =>
    $"{Mask(d.Id.Value)}  container {d.ContainerId?.ToString() ?? "none"}  IsActive={d.IsActive}";

// Only the target's address, and a replacement's once found, are shown. Every other
// Bluetooth device on the host is masked.
string Mask(string id) =>
    Regex.Replace(id, "(?i)[0-9a-f]{12}", m =>
        BluetoothAddress.TryParse(m.Value, out var a) && visible.Contains(a.Value) ? m.Value : "<addr12>");
