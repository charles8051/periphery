using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

// ─────────────────────────────────────────────────────────────────────────────
// Asks Windows to keep a bonded LE peripheral connected, for as long as it runs.
//
//   dotnet run --project scratch/BleLinkHold [name] [seconds]
//
//   name     substring of the paired device's name (default "Periphery Bench")
//   seconds  how long to hold the session (default 120)
// ─────────────────────────────────────────────────────────────────────────────

string name = args.FirstOrDefault(a => !int.TryParse(a, out _)) ?? "Periphery Bench";
int seconds = args.Select(a => int.TryParse(a, out var v) ? v : 0).FirstOrDefault(v => v > 0, 120);
var clock = Stopwatch.StartNew();
void Log(string message) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s  hold       {message}");

var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
var match = paired.Where(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
if (match.Count != 1)
{
    Log($"expected one paired LE device named like '{name}', found {match.Count}");
    return 1;
}

using var device = await BluetoothLEDevice.FromIdAsync(match[0].Id);
if (device is null)
{
    Log("FromIdAsync returned null");
    return 1;
}

Log($"device {Mask(device.DeviceId)}  status {device.ConnectionStatus}");
device.ConnectionStatusChanged += (d, _) => Log($"ConnectionStatusChanged -> {d.ConnectionStatus}");

using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
session.SessionStatusChanged += (_, e) => Log($"SessionStatusChanged -> {e.Status} ({e.Error})");
session.MaintainConnection = true;
Log($"MaintainConnection set, holding {seconds} s");

await Task.Delay(TimeSpan.FromSeconds(seconds));
session.MaintainConnection = false;
Log($"released, status {device.ConnectionStatus}");
return 0;

static string Mask(string s) =>
    Regex.Replace(
        Regex.Replace(s, @"(?i)[0-9a-f]{2}([:\-][0-9a-f]{2}){5}", "<addr>"),
        @"(?i)[0-9a-f]{12}", "<addr12>");
