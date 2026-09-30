#if WINDOWS
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using InTheHand.Bluetooth;

namespace Periphery.Ble.InTheHand.Tests;

/// <summary>
/// Device-backed tests against the BLE bench (docs/patterns/ble-bench-testing.md). They run only
/// when <c>PERIPHERY_BLE_DEVICE_TESTS=1</c>, with <c>PERIPHERY_BLE_BENCH_ADDRESS</c> naming a bench
/// peripheral paired with this host and running a bench image, which serves Heart Rate and notifies
/// once a second. When enabled, a missing peripheral is a failure, never a skip.
/// The proxy test also needs <c>PERIPHERY_BLE_BENCH_JLINK</c>, the serial of the peripheral's J-Link,
/// and <c>nrfutil</c> on <c>PATH</c> or in <c>PERIPHERY_BLE_BENCH_NRFUTIL</c>: it halts the
/// peripheral to drop the link, then resets it.
/// </summary>
public class BleBenchDeviceTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("PERIPHERY_BLE_DEVICE_TESTS") == "1";

    private static readonly BluetoothUuid HeartRateService = BluetoothUuid.FromShortId(0x180D);
    private static readonly BluetoothUuid HeartRateMeasurement = BluetoothUuid.FromShortId(0x2A37);
    private static readonly BluetoothUuid BodySensorLocation = BluetoothUuid.FromShortId(0x2A38);

    private static async Task<DeviceInfo> FindBenchNodeAsync()
    {
        string? text = Environment.GetEnvironmentVariable("PERIPHERY_BLE_BENCH_ADDRESS");
        Assert.True(BluetoothAddress.TryParse(text, out var address),
            "Set PERIPHERY_BLE_BENCH_ADDRESS to the bench peripheral's address.");

        // The filters alone select the LE link node (#301, #302); the id check proves they did.
        var nodes = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Bluetooth)
            .WithMacAddress(PhysicalAddress.Parse(address.ToString()))
            .WithBluetoothTransport(BluetoothTransport.LowEnergy)
            .ToListAsync();
        var node = nodes.SingleOrDefault();
        Assert.True(node is not null, $"No paired LE node for {address}. Pair the bench peripheral with this host.");
        Assert.True(BluetoothAddress.TryParseInstanceId(node.Id.Value, out var parsed, out _) && parsed == address,
            $"'{node.Id.Value}' is not the link node of {address}.");
        return node;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Proxy_ConnectsWhilePresent_AndReconnectsAfterTheLinkDrops()
    {
        if (!Enabled) return;

        string? jlink = Environment.GetEnvironmentVariable("PERIPHERY_BLE_BENCH_JLINK");
        Assert.False(string.IsNullOrEmpty(jlink), "Set PERIPHERY_BLE_BENCH_JLINK to the bench peripheral's J-Link serial.");
        var node = await FindBenchNodeAsync();
        var profile = new DeviceProfile(f => f
            .OfCategory(DeviceCategory.Bluetooth)
            .WithMacAddress(node.MacAddress!)
            .WithBluetoothTransport(BluetoothTransport.LowEnergy), "bench");

        var opened = Channel.CreateUnbounded<BleSession>();
        var closed = Channel.CreateUnbounded<bool>();
        var failed = Channel.CreateUnbounded<BleException>();
        await using var proxy = await BleDeviceProxy.OpenAsync(profile);
        proxy.DeviceOpened += (_, session) => opened.Writer.TryWrite(session);
        proxy.DeviceClosed += (_, _) => closed.Writer.TryWrite(true);
        proxy.OpenFailed += (_, ex) => failed.Writer.TryWrite(ex);
        try
        {
            // Bounds that turn a stuck proxy into a failure; the proxy's own events are the
            // signals (ADR-0089 D5). A connect to a silent peripheral took about 23 s here.
            var first = proxy.Device ?? await opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Single(await ReadBodySensorLocationAsync(first));
            while (opened.Reader.TryRead(out _)) { }   // the first open, if it raced the subscription

            await NrfutilAsync("halt", jlink!);
            await closed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            // While the peripheral is silent, an attempt fails rather than opening a dead session.
            var failure = await failed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Contains("did not connect", failure.Message);
            Assert.False(proxy.IsOpen);

            await NrfutilAsync("reset", jlink!);
            var second = await opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(120));
            Assert.NotSame(first, second);
            Assert.Single(await ReadBodySensorLocationAsync(second));
        }
        finally
        {
            // Never leave the peripheral halted.
            await NrfutilAsync("reset", jlink!);
        }
    }

    private static async Task<byte[]> ReadBodySensorLocationAsync(BleSession session)
    {
        var service = await session.Gatt.GetPrimaryServiceAsync(HeartRateService);
        Assert.NotNull(service);
        var location = await service.GetCharacteristicAsync(BodySensorLocation);
        Assert.NotNull(location);
        return await location.ReadValueAsync() ?? [];
    }

    private static async Task NrfutilAsync(string verb, string serial)
    {
        string nrfutil = Environment.GetEnvironmentVariable("PERIPHERY_BLE_BENCH_NRFUTIL") ?? "nrfutil";
        var start = new ProcessStartInfo(nrfutil) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "device", verb, "--serial-number", serial })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(cts.Token);
        Assert.True(process.ExitCode == 0, $"nrfutil device {verb} exited {process.ExitCode}: {await process.StandardError.ReadToEndAsync()}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BenchPeripheral_JoinsTo32feet_AtItsOwnAddress()
    {
        if (!Enabled) return;

        var node = await FindBenchNodeAsync();
        var device = await node.ToBluetoothDeviceAsync();

        Assert.NotNull(device);
        // 32feet's Windows Id drops leading zeros, so the two meet as parsed addresses.
        Assert.True(BluetoothAddress.TryParse(device.Id, out var joined), $"32feet Id '{device.Id}' is not an address.");
        Assert.Equal(BleJoin.AddressOf(node).Address, joined);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BenchPeripheral_HeartRate_ReadsBodySensorLocation_AndNotifies()
    {
        if (!Enabled) return;

        var node = await FindBenchNodeAsync();
        var device = await node.ToBluetoothDeviceAsync();
        Assert.NotNull(device);

        await device.Gatt.ConnectAsync();
        try
        {
            var service = await device.Gatt.GetPrimaryServiceAsync(HeartRateService);
            Assert.NotNull(service);

            var location = await service.GetCharacteristicAsync(BodySensorLocation);
            Assert.NotNull(location);
            Assert.Single(await location.ReadValueAsync());

            var measurement = await service.GetCharacteristicAsync(HeartRateMeasurement);
            Assert.NotNull(measurement);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            measurement.CharacteristicValueChanged += (_, e) => received.TrySetResult(e.Value ?? []);
            await measurement.StartNotificationsAsync();

            // The firmware notifies once a second; the bound only turns a silent peripheral into a failure.
            byte[] notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await measurement.StopNotificationsAsync();

            Assert.True(notification.Length >= 2, "A Heart Rate Measurement carries a flags byte and a rate.");
        }
        finally
        {
            device.Gatt.Disconnect();
        }
    }
}
#endif
