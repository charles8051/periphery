using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using ItH = InTheHand.Bluetooth;

// ─────────────────────────────────────────────────────────────────────────────
// Read-only survey of bonded Bluetooth devices through WinRT and 32feet.
// Cache-only: no connection is opened, so no link state changes. Addresses are
// masked, because output from this probe gets pasted into public documents.
//
//   dotnet run --project scratch/BleOsProbe [watchSeconds]
// ─────────────────────────────────────────────────────────────────────────────

int watchSeconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 5;

string[] aepProps =
[
    "System.Devices.Aep.DeviceAddress",
    "System.Devices.Aep.IsConnected",
    "System.Devices.Aep.IsPaired",
    "System.Devices.Aep.IsPresent",
    "System.Devices.Aep.Bluetooth.Le.IsConnectable",
    // ContainerId is deliberately absent. It is a stable per-device GUID, and
    // Mask only recognises address formats, so it would print verbatim.
    "System.Devices.Aep.ProtocolId",
];

Section("1. Paired LE association endpoints (DeviceInformationKind.AssociationEndpoint)");
var le = await DeviceInformation.FindAllAsync(
    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), aepProps, DeviceInformationKind.AssociationEndpoint);
Console.WriteLine($"  count {le.Count}");
foreach (var info in le)
{
    Console.WriteLine($"  Id    {Mask(info.Id)}");
    Console.WriteLine($"  Kind  {info.Kind}");
    foreach (var p in aepProps)
        Console.WriteLine($"    {p,-48} {Mask(Fmt(info.Properties.TryGetValue(p, out var v) ? v : null))}");
}

Section("2. BluetoothLEDevice per endpoint (cached GATT only)");
foreach (var info in le)
{
    using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
    if (dev is null) { Console.WriteLine($"  FromIdAsync returned null for {Mask(info.Id)}"); continue; }

    string addrHex = dev.BluetoothAddress.ToString("X12");
    string? aepAddr = info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a)
        ? (a as string)?.Replace(":", "").ToUpperInvariant()
        : null;
    Console.WriteLine($"  DeviceId               {Mask(dev.DeviceId)}");
    Console.WriteLine($"  BluetoothAddressType   {dev.BluetoothAddressType}  ({AddrKind(dev.BluetoothAddress, dev.BluetoothAddressType)})");
    Console.WriteLine($"  address == AEP address {aepAddr == addrHex}");
    Console.WriteLine($"  address in Id          {info.Id.ToUpperInvariant().Replace(":", "").Contains(addrHex)}");
    Console.WriteLine($"  ConnectionStatus       {dev.ConnectionStatus}");
    Console.WriteLine($"  Pairing.IsPaired       {dev.DeviceInformation.Pairing.IsPaired}  ProtectionLevel {dev.DeviceInformation.Pairing.ProtectionLevel}");
    Console.WriteLine($"  Access                 {dev.DeviceAccessInformation.CurrentStatus}");

    var services = await dev.GetGattServicesAsync(BluetoothCacheMode.Cached);
    Console.WriteLine($"  GetGattServicesAsync(Cached) -> {services.Status}, {services.Services.Count} service(s)");
    foreach (var svc in services.Services)
    {
        using (svc)
        {
            var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
            Console.WriteLine($"    {svc.Uuid}  characteristics(Cached) -> {chars.Status}, {chars.Characteristics.Count}");
        }
    }
}

Section("3. Paired BR/EDR association endpoints");
var br = await DeviceInformation.FindAllAsync(
    BluetoothDevice.GetDeviceSelectorFromPairingState(true), aepProps, DeviceInformationKind.AssociationEndpoint);
Console.WriteLine($"  count {br.Count}");
foreach (var info in br)
{
    Console.WriteLine($"  Id    {Mask(info.Id)}");
    Console.WriteLine($"    IsConnected {Fmt(info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) ? c : null)}");
    using var dev = await BluetoothDevice.FromIdAsync(info.Id);
    if (dev is not null)
        Console.WriteLine($"    BluetoothDevice.DeviceId {Mask(dev.DeviceId)}  ConnectionStatus {dev.ConnectionStatus}");
}

Section("4. InTheHand.BluetoothLE Bluetooth.GetPairedDevicesAsync()");
var paired = await ItH.Bluetooth.GetPairedDevicesAsync();
Console.WriteLine($"  count {paired.Count}");
var leAddrs = new List<string>();
foreach (var info in le)
{
    using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
    if (dev is not null) leAddrs.Add(dev.BluetoothAddress.ToString("X12"));
}
var brAddrs = new List<string>();
foreach (var info in br)
{
    using var dev = await BluetoothDevice.FromIdAsync(info.Id);
    if (dev is not null) brAddrs.Add(dev.BluetoothAddress.ToString("X12"));
}
foreach (var d in paired)
{
    string shape = Regex.Replace(d.Id, "[0-9A-F]", "H");
    shape = Regex.Replace(shape, "[0-9a-f]", "h");
    Console.WriteLine($"  Id shape {shape}  length {d.Id.Length}  IsPaired {d.IsPaired}");
    Console.WriteLine($"    equals an AEP Id           {le.Concat(br).Any(i => string.Equals(i.Id, d.Id, StringComparison.OrdinalIgnoreCase))}");
    Console.WriteLine($"    equals an LE address (X12) {leAddrs.Any(x => string.Equals(x, d.Id, StringComparison.OrdinalIgnoreCase))}  (ordinal-exact {leAddrs.Contains(d.Id)})");
    Console.WriteLine($"    equals a BR address (X12)  {brAddrs.Any(x => string.Equals(x, d.Id, StringComparison.OrdinalIgnoreCase))}");
    var back = await ItH.BluetoothDevice.FromIdAsync(d.Id);
    Console.WriteLine($"    BluetoothDevice.FromIdAsync(Id) round-trips {back is not null && back.Id == d.Id}");
}

Section($"5. AEP DeviceWatcher on paired LE, IsConnected requested, {watchSeconds}s");
var watcher = DeviceInformation.CreateWatcher(
    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), aepProps, DeviceInformationKind.AssociationEndpoint);
var started = DateTime.UtcNow;
string T() => $"+{(DateTime.UtcNow - started).TotalSeconds,5:F1}s";
watcher.Added += (_, i) => Console.WriteLine($"  {T()} Added   {Mask(i.Id)}");
watcher.Updated += (_, u) => Console.WriteLine(
    $"  {T()} Updated {Mask(u.Id)}  {string.Join(", ", u.Properties.Select(kv => $"{kv.Key}={Mask(Fmt(kv.Value))}"))}");
watcher.Removed += (_, r) => Console.WriteLine($"  {T()} Removed {Mask(r.Id)}");
watcher.EnumerationCompleted += (_, _) => Console.WriteLine($"  {T()} EnumerationCompleted");
watcher.Start();
await Task.Delay(TimeSpan.FromSeconds(watchSeconds));
watcher.Stop();
Console.WriteLine($"  stopped, status {watcher.Status}");
return 0;

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('-', 78));
}

static string Fmt(object? v) => v switch
{
    null => "<absent>",
    string[] arr => string.Join("|", arr),
    _ => v.ToString() ?? "<null>",
};

// No word boundaries: a WinRT Id glues the radio address straight onto
// "BluetoothLE", and a boundary assertion lets the first octets through.
static string Mask(string s) =>
    Regex.Replace(
        Regex.Replace(s, @"(?i)[0-9a-f]{2}([:\-][0-9a-f]{2}){5}", "<addr>"),
        @"(?i)[0-9a-f]{12}", "<addr12>");

static string AddrKind(ulong addr, BluetoothAddressType type)
{
    if (type == BluetoothAddressType.Public) return "public";
    return ((addr >> 46) & 0b11) switch
    {
        0b11 => "random static",
        0b01 => "resolvable private form",
        0b00 => "non-resolvable private form",
        _ => "reserved top bits 10",
    };
}
