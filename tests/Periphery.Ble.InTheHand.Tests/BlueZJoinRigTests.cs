#if !WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using InTheHand.Bluetooth;

namespace Periphery.Ble.InTheHand.Tests;

/// <summary>
/// The join on Linux against a real BlueZ (ADR-0085, ADR-0091). These run only on the Linux device
/// rig, where <c>PERIPHERY_LINUX_DEVICE_TESTS=1</c> and <c>periphery-btvirt.service</c> runs two
/// virtual controllers: hci0 <c>00:AA:01:00:00:00</c> bonded to hci1 <c>00:AA:01:01:00:01</c>. hci1's
/// bluetoothd serves Device Information, whose PnP ID the test reads over GATT. On the rig a missing
/// fixture is a failure, never a skip (ADR-0089).
/// </summary>
public class BlueZJoinRigTests
{
    private const string Central = "00:AA:01:00:00:00";
    private const string Peripheral = "00:AA:01:01:00:01";

    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bond_JoinsTo32feet_AndReadsThePeersPnpIdOverGatt()
    {
        if (!Enabled) return;

        var devices = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
        var bond = devices.SingleOrDefault(d => d.Id.Value == $"bluez:{Central}/{Peripheral}");
        Assert.True(bond is not null, $"No bond to {Peripheral} on {Central}. Is periphery-btvirt running and the pair bonded?");

        // Positive control: BlueZ's own record of the peer's Device ID, which PnP ID serves.
        string info = await RunToEndAsync("bluetoothctl", "info", Peripheral);
        var modalias = Regex.Match(info, "Modalias: usb:v(?<v>[0-9A-F]{4})p(?<p>[0-9A-F]{4})d(?<d>[0-9A-F]{4})");
        Assert.True(modalias.Success, $"BlueZ reports no USB modalias for {Peripheral}: {info}");

        // hci0 can connect only while hci1 advertises, and bluetoothctl holds the advertisement until
        // it quits.
        using var advertiser = await AdvertiseFromPeripheralAsync();
        try
        {
            var device = await bond.ToBluetoothDeviceAsync();
            Assert.True(device is not null, $"32feet did not resolve {Peripheral}.");
            Assert.Equal(BluetoothAddress.Parse(Peripheral), BluetoothAddress.Parse(device.Id));

            await device.Gatt.ConnectAsync();
            try
            {
                var service = await device.Gatt.GetPrimaryServiceAsync(BluetoothUuid.FromShortId(0x180A));
                Assert.True(service is not null, "The peer serves no Device Information service.");
                var pnpId = await service.GetCharacteristicAsync(BluetoothUuid.FromShortId(0x2A50));
                Assert.True(pnpId is not null, "Device Information has no PnP ID characteristic.");

                // Vendor ID source 0x02 (USB), then vendor, product and version, little-endian.
                byte[] expected = [0x02, .. LittleEndian(modalias.Groups["v"].Value), .. LittleEndian(modalias.Groups["p"].Value), .. LittleEndian(modalias.Groups["d"].Value)];
                Assert.Equal(expected, await pnpId.ReadValueAsync());
            }
            finally
            {
                device.Gatt.Disconnect();
            }
        }
        finally
        {
            advertiser.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Proxy_ConnectsWhilePresent_AndReconnectsAfterTheLinkDrops()
    {
        if (!Enabled) return;

        var profile = new DeviceProfile(f => f
            .OfCategory(DeviceCategory.Bluetooth)
            .WithMacAddress(PhysicalAddress.Parse(Peripheral.Replace(':', '-')))
            .WithBluetoothTransport(BluetoothTransport.LowEnergy), "btvirt");

        var opened = Channel.CreateUnbounded<BleSession>();
        var closed = Channel.CreateUnbounded<bool>();
        var failed = Channel.CreateUnbounded<BleException>();
        var advertiser = await AdvertiseFromPeripheralAsync();
        try
        {
            await using var proxy = await BleDeviceProxy.OpenAsync(profile);
            proxy.DeviceOpened += (_, session) => opened.Writer.TryWrite(session);
            proxy.DeviceClosed += (_, _) => closed.Writer.TryWrite(true);
            proxy.OpenFailed += (_, ex) => failed.Writer.TryWrite(ex);

            // Bounds that turn a stuck proxy into a failure; its own events are the signals (ADR-0089 D5).
            var first = proxy.Device ?? await opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(7, (await ReadPnpIdAsync(first)).Length);
            while (opened.Reader.TryRead(out _)) { }   // the first open, if it raced the subscription

            // hci1 stops advertising and drops the link, so hci0 has nothing to reconnect to.
            advertiser.Kill(entireProcessTree: true);
            await RunToEndAsync("bash", "-c", $"{{ echo 'select {Peripheral}'; sleep 1; echo 'disconnect {Central}'; sleep 3; echo quit; }} | bluetoothctl");
            await closed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            var failure = await failed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Contains("did not connect", failure.Message);
            Assert.False(proxy.IsOpen);

            advertiser = await AdvertiseFromPeripheralAsync();
            var second = await opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(120));
            Assert.NotSame(first, second);
            Assert.Equal(7, (await ReadPnpIdAsync(second)).Length);
        }
        finally
        {
            advertiser.Kill(entireProcessTree: true);
        }
    }

    private static async Task<byte[]> ReadPnpIdAsync(BleSession session)
    {
        var service = await session.Gatt.GetPrimaryServiceAsync(BluetoothUuid.FromShortId(0x180A));
        Assert.True(service is not null, "The peer serves no Device Information service.");
        var pnpId = await service.GetCharacteristicAsync(BluetoothUuid.FromShortId(0x2A50));
        Assert.True(pnpId is not null, "Device Information has no PnP ID characteristic.");
        return await pnpId.ReadValueAsync() ?? [];
    }

    private static byte[] LittleEndian(string hex)
    {
        ushort value = ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return [(byte)value, (byte)(value >> 8)];
    }

    // Starts bluetoothctl advertising from hci1 and returns once BlueZ has registered the
    // advertisement. The process keeps it registered until it exits or is killed.
    private static async Task<Process> AdvertiseFromPeripheralAsync()
    {
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"{{ echo 'select {Peripheral}'; sleep 1; echo 'advertise peripheral'; sleep 300; echo quit; }} | bluetoothctl");
        var process = Process.Start(start)!;

        // A safety net, not an assertion (ADR-0089 D5). The wait ends on bluetoothctl's own line.
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await process.StandardOutput.ReadLineAsync(safety.Token) is { } line)
        {
            if (line.Contains("Advertising object registered", StringComparison.Ordinal))
                return process;
        }
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("bluetoothctl exited without registering an advertisement.");
    }

    private static async Task<string> RunToEndAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string output = await process.StandardOutput.ReadToEndAsync(safety.Token);
        await process.WaitForExitAsync(safety.Token);
        return output;
    }
}
#endif
