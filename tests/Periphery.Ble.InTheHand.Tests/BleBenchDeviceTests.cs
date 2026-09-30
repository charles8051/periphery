#if WINDOWS
using System.Net.NetworkInformation;
using InTheHand.Bluetooth;

namespace Periphery.Ble.InTheHand.Tests;

/// <summary>
/// Device-backed tests against the BLE bench (docs/patterns/ble-bench-testing.md). They run only
/// when <c>PERIPHERY_BLE_DEVICE_TESTS=1</c>, with <c>PERIPHERY_BLE_BENCH_ADDRESS</c> naming a bench
/// peripheral paired with this host and running a bench image, which serves Heart Rate and notifies
/// once a second. When enabled, a missing peripheral is a failure, never a skip.
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
