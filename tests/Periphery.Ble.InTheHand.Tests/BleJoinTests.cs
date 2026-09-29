namespace Periphery.Ble.InTheHand.Tests;

/// <summary>
/// Unit tests for <see cref="BleJoin"/>, the pure half of the ADR-0085 D7 join. Strings and values
/// only, so they run on any host, with the join's source compiled in on net10.0. Addresses are
/// synthetic.
/// </summary>
public class BleJoinTests
{
    private static DeviceInfo Node(string id) => new() { Id = id, Name = "Test peripheral", Category = DeviceCategory.Bluetooth };

    // ── LeAddressOf ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"BTHLE\DEV_C1D2E3F4A5B6\7&11111111&0&C1D2E3F4A5B6")]
    [InlineData(@"BTHLE\Dev_c1d2e3f4a5b6\7&11111111&0&c1d2e3f4a5b6")]   // the casing of a live arrival
    public void LeAddressOf_LeLinkNode_ReadsItsAddress(string id)
    {
        Assert.Equal(new BluetoothAddress(0xC1D2E3F4A5B6), BleJoin.LeAddressOf(Node(id)));
    }

    [Theory]
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5\7&2A1B3C4D&0&BLUETOOTHDEVICE_A0B1C2D3E4F5")]   // BR/EDR
    [InlineData(@"BTHLEDEVICE\{0000180D-0000-1000-8000-00805F9B34FB}_C1D2E3F4A5B6\8&0&0010")] // a GATT service node
    [InlineData(@"USB\VID_046D&PID_C52B\5&3A1B2C&0&2")]
    public void LeAddressOf_AnyOtherNode_Throws(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => BleJoin.LeAddressOf(Node(id)));
        Assert.Equal("device", ex.ParamName);
    }

    [Fact]
    public void LeAddressOf_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BleJoin.LeAddressOf(null!));
    }

    // ── ToBluetoothDeviceId ────────────────────────────────────────────

    [Fact]
    public void ToBluetoothDeviceId_WritesTwelveHexDigits_KeepingLeadingZeros()
    {
        Assert.Equal("001A2B3C4D5E", BleJoin.ToBluetoothDeviceId(new BluetoothAddress(0x001A2B3C4D5E)));
    }

    [Fact]
    public void ToBluetoothDeviceId_ParsesBackToTheSameAddress_As32feetsUnpaddedIdDoes()
    {
        // 32feet's own Windows Id formats with "X6", so an address that begins 00 comes back as ten
        // digits. The join and 32feet's Id meet as parsed addresses.
        var address = new BluetoothAddress(0x001A2B3C4D5E);

        Assert.Equal(address, BluetoothAddress.Parse(BleJoin.ToBluetoothDeviceId(address)));
        Assert.Equal(address, BluetoothAddress.Parse("1A2B3C4D5E"));
    }
}
