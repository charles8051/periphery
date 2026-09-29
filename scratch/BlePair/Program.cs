using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

// ─────────────────────────────────────────────────────────────────────────────
// Pairs or unpairs a BLE bench peripheral.
//
//   dotnet run --project scratch/BlePair pair <name|address> [seconds]
//   dotnet run --project scratch/BlePair unpair <address>
//
//   pair    scans for an advertiser whose local name is exactly <name>, or whose address is
//           <address>, and pairs the first one it hears, accepting Just Works. An address is
//           needed when two units share a name. Gives up after [seconds] (default 20).
//   unpair  removes the bond with the paired LE device whose address, as its BTHLE\DEV_ node
//           carries it, is <address> (AA:BB:CC:DD:EE:FF or twelve hex digits).
// ─────────────────────────────────────────────────────────────────────────────

var clock = Stopwatch.StartNew();
void Log(string message) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F3}s  pair       {message}");

if (args.Length < 2 || args[0] is not ("pair" or "unpair"))
{
    Console.Error.WriteLine("usage: BlePair pair <name> [seconds] | BlePair unpair <address>");
    return 2;
}

return args[0] == "pair" ? await PairAsync(args[1], args.Length > 2 ? int.Parse(args[2]) : 20) : await UnpairAsync(args[1]);

async Task<int> PairAsync(string name, int seconds)
{
    ulong? wanted = TryParseAddress(name);
    var heard = new TaskCompletionSource<(ulong Address, BluetoothAddressType Type)>(TaskCreationOptions.RunContinuationsAsynchronously);
    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
    watcher.Received += (_, e) =>
    {
        if (wanted is ulong a ? e.BluetoothAddress == a : e.Advertisement.LocalName == name)
            heard.TrySetResult((e.BluetoothAddress, e.BluetoothAddressType));
    };
    watcher.Start();
    var winner = await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
    watcher.Stop();
    if (winner != heard.Task)
    {
        Log($"no advertiser matching '{name}' in {seconds} s");
        return 1;
    }

    var (address, type) = heard.Task.Result;
    Log($"heard '{name}' at {Format(address)} ({type})");

    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, type);
    if (device is null)
    {
        Log("FromBluetoothAddressAsync returned null");
        return 1;
    }

    var pairing = device.DeviceInformation.Pairing;
    if (pairing.IsPaired)
    {
        Log("already paired");
        return 0;
    }

    pairing.Custom.PairingRequested += (_, e) =>
    {
        Log($"PairingRequested {e.PairingKind}, accepting");
        e.Accept();
    };
    // Encryption, not the default: the bench peripheral pairs Just Works, which cannot give the
    // MITM protection the default level asks for, so the peripheral rejects the request.
    var result = await pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption);
    Log($"PairAsync -> {result.Status}, protection {result.ProtectionLevelUsed}");
    return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired ? 0 : 1;
}

async Task<int> UnpairAsync(string text)
{
    if (TryParseAddress(text) is not ulong address)
    {
        Log($"'{text}' is not an address");
        return 2;
    }

    var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
    foreach (var info in paired)
    {
        using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
        if (device?.BluetoothAddress != address)
            continue;

        var result = await device.DeviceInformation.Pairing.UnpairAsync();
        Log($"UnpairAsync {Format(address)} -> {result.Status}");
        return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired ? 0 : 1;
    }

    Log($"no paired LE device at {Format(address)}");
    return 1;
}

static ulong? TryParseAddress(string text)
{
    string hex = text.Replace(":", "").Replace("-", "");
    return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out ulong v)
        ? v
        : null;
}

static string Format(ulong address) =>
    string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (40 - 8 * i)) & 0xFF).ToString("X2")));
