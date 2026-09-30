using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.Json;
using Periphery.MacOS.Bluetooth;
using Periphery.MacOS.Bluetooth.Core;

namespace Periphery.Tests.MacOS;

/// <summary>
/// ADR-0093 against a real Mac. These run only with <c>PERIPHERY_MACOS_DEVICE_TESTS=1</c>, from a
/// process that has Bluetooth permission, which over SSH means through a terminal app that has it.
/// <c>system_profiler SPBluetoothDataType</c> needs no permission and is the ground truth. When
/// enabled, a missing permission or a Mac with no bonds is a failure, never a skip (ADR-0089).
/// </summary>
[SupportedOSPlatform("macos")]
public class IOBluetoothRigTests
{
    private static bool Enabled =>
        OperatingSystem.IsMacOS()
        && Environment.GetEnvironmentVariable("PERIPHERY_MACOS_DEVICE_TESTS") == "1";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EveryBondSystemProfilerLists_EnumeratesUnderBluetooth_WithItsConnection()
    {
        if (!Enabled) return;

        Assert.True(IOBluetoothInterop.ReadAuthorization() == BluetoothAuthorization.AllowedAlways,
            $"Bluetooth authorization is {IOBluetoothInterop.ReadAuthorization()}. Run from a terminal that has Bluetooth access.");

        var expected = await SystemProfilerBondsAsync();
        Assert.True(expected.Count > 0, "system_profiler lists no bonded device. Pair one with this Mac.");

        var devices = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();

        foreach (var (address, connected, le) in expected)
        {
            var bond = devices.SingleOrDefault(d => d.Id.Value == IOBluetoothInventory.IdPrefix + address);
            Assert.True(bond is not null,
                $"{address} is not among {devices.Count} Bluetooth devices: {string.Join(", ", devices.Select(d => d.Id.Value))}");
            Assert.Equal(connected, bond!.IsActive);
            Assert.Equal(PhysicalAddress.Parse(address.Replace(':', '-')), bond.MacAddress);
            // A connected LE device's HID node shows its transport (ADR-0093 D3 amendment).
            if (le)
                Assert.True(bond.BluetoothTransports?.HasFlag(BluetoothTransports.LowEnergy),
                    $"{address} is connected over BLE, but reports {bond.BluetoothTransports}.");
        }

        // The registry's IOBluetoothDevice, the Mac's own incoming serial service, is not a bond.
        Assert.All(devices, d => Assert.StartsWith(IOBluetoothInventory.IdPrefix, d.Id.Value));
    }

    // (address in upper-case colon form, connected, connected over BLE), from system_profiler's JSON.
    private static async Task<List<(string Address, bool Connected, bool LowEnergy)>> SystemProfilerBondsAsync()
    {
        var start = new ProcessStartInfo("system_profiler") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-json");
        start.ArgumentList.Add("SPBluetoothDataType");
        using var process = Process.Start(start)!;
        // A safety net, not an assertion (ADR-0089 D5).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string json = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        var bonds = new List<(string, bool, bool)>();
        var controller = JsonDocument.Parse(json).RootElement.GetProperty("SPBluetoothDataType")[0];
        foreach (var (key, connected) in new[] { ("device_connected", true), ("device_not_connected", false) })
        {
            if (!controller.TryGetProperty(key, out var list))
                continue;
            foreach (var entry in list.EnumerateArray())
            foreach (var device in entry.EnumerateObject())
            {
                bool le = device.Value.TryGetProperty("device_services", out var services)
                    && services.GetString()!.Contains("BLE", StringComparison.Ordinal);
                bonds.Add((device.Value.GetProperty("device_address").GetString()!.ToUpperInvariant(), connected, connected && le));
            }
        }
        return bonds;
    }
}
