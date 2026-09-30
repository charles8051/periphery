using System.Collections.Concurrent;
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Connection_RaisesActivatedThenDeactivated_AndNoLinkNodeAppears()
    {
        if (!Enabled) return;

        string bond = $"bluez:{Central}/{Peripheral}";
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deactivated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();

        await using var watcher = Devices.Watch().OfCategory(DeviceCategory.Bluetooth);
        watcher.Appeared += (_, e) => seen.Enqueue(e.Device.Id.Value);
        watcher.Activated += (_, e) =>
        {
            seen.Enqueue(e.Device.Id.Value);
            if (e.Device.Id.Value == bond)
                activated.TrySetResult();
        };
        watcher.Deactivated += (_, e) =>
        {
            if (e.Device.Id.Value == bond && activated.Task.IsCompleted)
                deactivated.TrySetResult();
        };
        await watcher.StartAsync();

        // hci1 advertises, hci0 connects to it and holds the link, then disconnects. The pauses are
        // bluetoothctl's; the test waits on the watcher's edges.
        var drive = RunAsync("bash", "-c",
            "{ echo 'select 00:AA:01:01:00:01'; sleep 1; echo 'advertise peripheral'; sleep 2; "
            + "echo 'select 00:AA:01:00:00:00'; sleep 1; echo 'connect 00:AA:01:01:00:01'; sleep 6; "
            + "echo 'disconnect 00:AA:01:01:00:01'; sleep 3; echo 'select 00:AA:01:01:00:01'; sleep 1; "
            + "echo 'advertise off'; sleep 1; echo quit; } | bluetoothctl");

        // A safety net that bounds a failure; the edges are the signals (ADR-0089 D5).
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var first = await Task.WhenAny(activated.Task, drive).WaitAsync(safety.Token);
        Assert.True(first == activated.Task, $"No Activated for {bond} before bluetoothctl finished. It said: {await drive}");

        var during = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
        Assert.True(during.Single(d => d.Id.Value == bond).IsActive);
        Assert.DoesNotContain(during, d => IsLinkNode(d.Id.Value));

        await deactivated.Task.WaitAsync(safety.Token);
        string output = await drive;

        // Positive control: BlueZ itself reports the connection the edges describe.
        Assert.Contains("Connection successful", output);
        Assert.DoesNotContain(seen, IsLinkNode);
    }

    private static bool IsLinkNode(string id) => id.Contains("/hci0:") || id.Contains("/hci1:");

    private static Task<string> BluetoothctlAsync(params string[] args) => RunAsync("bluetoothctl", args);

    private static async Task<string> RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        // A safety net, not an assertion (ADR-0089 D5).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string output = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return output;
    }
}
