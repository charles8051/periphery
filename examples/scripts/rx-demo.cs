#:property TargetFramework=net10.0
#:package Periphery@1.0.0-alpha.*
#:package System.Reactive@6.0.1

// rx-demo.cs — monitor device events for a given category
// Run:  dotnet run rx-demo.cs <category>
//       dotnet run rx-demo.cs Usb

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Periphery;

if (args.Length == 0 || !Enum.TryParse<DeviceCategory>(args[0], ignoreCase: true, out var category))
{
    Console.Error.WriteLine("Usage: dotnet run rx-demo.cs <category>");
    Console.Error.WriteLine($"Categories: {string.Join(", ", Enum.GetNames<DeviceCategory>())}");
    return 1;
}

Console.WriteLine($"Watching {category} — press Ctrl+C to stop.");

await using var watcher = Devices.Watch();
var tracker = watcher.AddTracker(f => f.OfCategory(category));

using var sub = tracker
    .Select(s => $"  [{s.ActivityStatus,-12}]  {s.Device?.Name ?? s.Device?.Id ?? "—"}")
    .Subscribe(Console.WriteLine);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await watcher.StartAsync(cts.Token);
try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }

Console.WriteLine("Stopped.");
return 0;