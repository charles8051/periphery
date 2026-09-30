using System.Text.Json;

namespace Periphery.Tests;

/// <summary>
/// <see cref="DeviceFilter.WithBluetoothTransport"/> and its spec replay (issue #302). Devices are
/// built by hand. Where their transports come from is each platform's own test.
/// </summary>
public class BluetoothTransportFilterTests
{
    private static DeviceInfo Peripheral(string id, BluetoothTransports? transports) => new()
    {
        Id = id,
        Category = DeviceCategory.Bluetooth,
        BusType = BusType.Bluetooth,
        BluetoothTransports = transports,
    };

    private static readonly DeviceInfo LeOnly = Peripheral("le", BluetoothTransports.LowEnergy);
    private static readonly DeviceInfo ClassicOnly = Peripheral("classic", BluetoothTransports.BrEdr);
    private static readonly DeviceInfo DualMode = Peripheral("dual", BluetoothTransports.LowEnergy | BluetoothTransports.BrEdr);
    private static readonly DeviceInfo UnknownTransport = Peripheral("unknown", BluetoothTransports.None);
    private static readonly DeviceInfo NotAPeripheral = Peripheral("service node", null);

    private static readonly DeviceInfo[] All = [LeOnly, ClassicOnly, DualMode, UnknownTransport, NotAPeripheral];

    [Theory]
    [InlineData(BluetoothTransport.LowEnergy, new[] { "le", "dual" })]
    [InlineData(BluetoothTransport.BrEdr, new[] { "classic", "dual" })]
    public void MatchesPeripheralsKnownToSupportTheTransport(BluetoothTransport transport, string[] expected)
    {
        var filter = new DeviceFilter().WithBluetoothTransport(transport);

        Assert.Equal(expected, All.Where(filter.Matches).Select(d => d.Id.Value));
    }

    [Theory]
    [InlineData(BluetoothTransport.Unknown)]
    [InlineData((BluetoothTransport)7)]
    public void ATransportMustBeNamed(BluetoothTransport transport) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceFilter().WithBluetoothTransport(transport));

    [Fact]
    public void Spec_BindsFromJson_AndReplays()
    {
        var spec = JsonSerializer.Deserialize(
            """{ "bluetoothTransport": "LowEnergy" }""",
            DeviceFilterSpecJsonContext.Default.DeviceFilterSpec)!;

        Assert.Equal(BluetoothTransport.LowEnergy, spec.BluetoothTransport);
        var filter = new DeviceFilter().Apply(spec);
        Assert.Equal(new[] { "le", "dual" }, All.Where(filter.Matches).Select(d => d.Id.Value));
    }

    [Fact]
    public void Spec_WithUnknown_Throws()
    {
        var spec = new DeviceFilterSpec { BluetoothTransport = BluetoothTransport.Unknown };

        var ex = Assert.Throws<ArgumentException>(() => new DeviceFilter().Apply(spec));
        Assert.Contains(nameof(DeviceFilterSpec.BluetoothTransport), ex.Message);
    }

    [Fact]
    public void DeviceInfo_SerializesTransportsByName()
    {
        string json = JsonSerializer.Serialize(DualMode, DeviceInfoJsonContext.Default.DeviceInfo);

        Assert.Contains("\"bluetoothTransports\":\"BrEdr, LowEnergy\"", json);
        Assert.DoesNotContain("bluetoothTransports", JsonSerializer.Serialize(NotAPeripheral, DeviceInfoJsonContext.Default.DeviceInfo));
    }
}
