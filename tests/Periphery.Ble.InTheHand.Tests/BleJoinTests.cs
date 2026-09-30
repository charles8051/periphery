using System.Net.NetworkInformation;

namespace Periphery.Ble.InTheHand.Tests;

/// <summary>
/// Unit tests for <see cref="BleJoin"/>, the pure half of the ADR-0085 D7 join. Strings and values
/// only, so they run on any host. Addresses are synthetic.
/// </summary>
public class BleJoinTests
{
    private static DeviceInfo Node(string id, string? mac = null, BluetoothTransports? transports = null) => new()
    {
        Id = id,
        Name = "Test peripheral",
        Category = DeviceCategory.Bluetooth,
        MacAddress = mac is null ? null : PhysicalAddress.Parse(mac),
        BluetoothTransports = transports,
    };

    // ── Windows: the LE link node ──────────────────────────────────────

    [Theory]
    [InlineData(@"BTHLE\DEV_C1D2E3F4A5B6\7&11111111&0&C1D2E3F4A5B6")]
    [InlineData(@"BTHLE\Dev_c1d2e3f4a5b6\7&11111111&0&c1d2e3f4a5b6")]   // the casing of a live arrival
    public void AddressOf_WindowsLeLinkNode_ReadsItsAddress(string id)
    {
        Assert.Equal((new BluetoothAddress(0xC1D2E3F4A5B6), BleJoinPlatform.Windows), BleJoin.AddressOf(Node(id)));
    }

    // ── Linux: the BlueZ bond (ADR-0091) ───────────────────────────────

    [Fact]
    public void AddressOf_BlueZBond_ReadsItsMacAddress()
    {
        var bond = Node("bluez:00:AA:01:00:00:00/C1:D2:E3:F4:A5:B6", "C1-D2-E3-F4-A5-B6");

        Assert.Equal((new BluetoothAddress(0xC1D2E3F4A5B6), BleJoinPlatform.Linux), BleJoin.AddressOf(bond));
    }

    [Theory]
    [InlineData(BluetoothTransports.LowEnergy)]
    [InlineData(BluetoothTransports.LowEnergy | BluetoothTransports.BrEdr)]
    [InlineData(BluetoothTransports.None)]   // BlueZ revealed nothing
    [InlineData(BluetoothTransports.BrEdr)]  // before 5.84, Class alone does not rule LE out (#302)
    public void AddressOf_BlueZBond_IsAccepted_WhateverItsTransports(BluetoothTransports transports)
    {
        var bond = Node("bluez:00:AA:01:00:00:00/C1:D2:E3:F4:A5:B6", "C1-D2-E3-F4-A5-B6", transports);

        Assert.Equal(BleJoinPlatform.Linux, BleJoin.AddressOf(bond).Platform);
    }

    [Theory]
    [InlineData("bluez:00:AA:01:00:00:00/C1:D2:E3:F4:A5:B6", null)]            // no address
    [InlineData("/sys/devices/virtual/net/eth0", "C1-D2-E3-F4-A5-B6")]           // a MAC, but not a bond
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5\7&2A1B3C4D&0&BLUETOOTHDEVICE_A0B1C2D3E4F5", null)] // BR/EDR
    [InlineData(@"BTHLEDEVICE\{0000180D-0000-1000-8000-00805F9B34FB}_C1D2E3F4A5B6\8&0&0010", null)] // a GATT service node
    [InlineData(@"USB\VID_046D&PID_C52B\5&3A1B2C&0&2", null)]
    public void AddressOf_AnythingElse_Throws(string id, string? mac)
    {
        var ex = Assert.Throws<ArgumentException>(() => BleJoin.AddressOf(Node(id, mac)));
        Assert.Equal("device", ex.ParamName);
    }

    [Fact]
    public void AddressOf_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BleJoin.AddressOf(null!));
    }

    // ── The id 32feet resolves by ──────────────────────────────────────

    [Fact]
    public void WindowsId_IsTwelveHexDigits_KeepingLeadingZeros()
    {
        Assert.Equal("001A2B3C4D5E", BleJoin.ToBluetoothDeviceId(new BluetoothAddress(0x001A2B3C4D5E), BleJoinPlatform.Windows));
    }

    [Fact]
    public void WindowsId_ParsesBackToTheSameAddress_As32feetsUnpaddedIdDoes()
    {
        // 32feet's own Windows Id formats with "X6", so an address that begins 00 comes back as ten
        // digits. The join and 32feet's Id meet as parsed addresses.
        var address = new BluetoothAddress(0x001A2B3C4D5E);

        Assert.Equal(address, BluetoothAddress.Parse(BleJoin.ToBluetoothDeviceId(address, BleJoinPlatform.Windows)));
        Assert.Equal(address, BluetoothAddress.Parse("1A2B3C4D5E"));
    }

    [Fact]
    public void LinuxId_IsBlueZsColonForm()
    {
        Assert.Equal("00:1A:2B:3C:4D:5E", BleJoin.ToBluetoothDeviceId(new BluetoothAddress(0x001A2B3C4D5E), BleJoinPlatform.Linux));
    }
}
