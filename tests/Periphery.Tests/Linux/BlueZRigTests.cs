using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Periphery.Tests.Linux;

/// <summary>
/// ADR-0091 against a real BlueZ. These run only on the Linux device rig, where
/// <c>PERIPHERY_LINUX_DEVICE_TESTS=1</c> and <c>periphery-btvirt.service</c> runs <c>btvirt -L -l2</c>:
/// hci0 (<c>00:AA:01:00:00:00</c>) bonded to hci1 (<c>00:AA:01:01:00:01</c>). On the rig a missing
/// fixture is a failure, never a skip (ADR-0089).
/// </summary>
public class BlueZRigTests
{
    private const string Central = "00:AA:01:00:00:00";
    private const string Peripheral = "00:AA:01:01:00:01";

    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BondedPeer_EnumeratesUnderBluetooth()
    {
        if (!Enabled) return;

        // Positive control: BlueZ itself reports the bond on the default controller, hci0.
        string bonded = await BluetoothctlAsync("devices", "Bonded");
        Assert.True(bonded.Contains(Peripheral, StringComparison.OrdinalIgnoreCase),
            $"BlueZ lists no bond to {Peripheral}. Is periphery-btvirt running, and has the pair smoke test bonded it? "
            + $"bluetoothctl said: {bonded}");

        var devices = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();

        var bond = devices.FirstOrDefault(d => d.Id.Value == $"bluez:{Central}/{Peripheral}");
        Assert.True(bond is not null,
            $"bluez:{Central}/{Peripheral} is not among {devices.Count} Bluetooth devices: "
            + string.Join(", ", devices.Select(d => d.Id.Value)));
        Assert.Equal(PhysicalAddress.Parse(Peripheral.Replace(':', '-')), bond!.MacAddress);
        Assert.Equal(BusType.Bluetooth, bond.BusType);
        Assert.Equal(DeviceStatus.OK, bond.Status);
        Assert.Equal($"/org/bluez/hci0/dev_{Peripheral.Replace(':', '_')}", bond.LocationPath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UsbOnlyQuery_DoesNotReachBlueZ()
    {
        if (!Enabled) return;

        var devices = await Devices.Enumerate().WithUsbId("0627", "0001").ToListAsync();

        Assert.DoesNotContain(devices, d => d.Id.Value.StartsWith("bluez:", StringComparison.Ordinal));
    }

    private static async Task<string> BluetoothctlAsync(params string[] args)
    {
        var start = new ProcessStartInfo("bluetoothctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        // A safety net, not an assertion: bluetoothctl answers in milliseconds (ADR-0089 D5).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string output = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return output;
    }
}
