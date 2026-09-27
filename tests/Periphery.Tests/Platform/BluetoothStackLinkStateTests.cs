using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="BluetoothStackLinkState"/> (issue #288): parsing the stack's
/// <c>BTH_DEVICE_INFO_LIST</c> and taking a BR/EDR link devnode's activity from it. Byte buffers
/// and records only, so they run anywhere. Addresses are synthetic.
/// </summary>
[SupportedOSPlatform("windows")]
public class BluetoothStackLinkStateTests
{
    private const ulong Keyboard = 0xA0B1C2D3E4F5;
    private const ulong Headset = 0x0C1D2E3F4A5B;
    private const ulong Mouse = 0xC1D2E3F4A5B6;

    private const uint Paired = 0x08;
    private const uint Connected = BluetoothStackLinkState.BDIF_CONNECTED;
    private const uint LeConnected = 0x01000000;

    private const string KeyboardLinkNode = @"BTHENUM\DEV_A0B1C2D3E4F5\7&2A1B3C4D&0&BLUETOOTHDEVICE_A0B1C2D3E4F5";
    private const string KeyboardServiceNode = @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B35B\7&2A1B3C4D&0&A0B1C2D3E4F5_C00000000";
    private const string MouseLinkNode = @"BTHLE\DEV_C1D2E3F4A5B6\7&11111111&0&C1D2E3F4A5B6";

    // BTH_DEVICE_INFO_LIST: byte-packed ULONG count @0, then 272-byte BTH_DEVICE_INFO entries @4
    // with flags @0 and address @8.
    private static byte[] DeviceList(params (ulong Address, uint Flags)[] devices)
    {
        var buffer = new byte[BluetoothStackLinkState.ListHeaderSize + devices.Length * BluetoothStackLinkState.DeviceInfoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)devices.Length);
        for (int i = 0; i < devices.Length; i++)
        {
            var entry = buffer.AsSpan(BluetoothStackLinkState.ListHeaderSize + i * BluetoothStackLinkState.DeviceInfoSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, devices[i].Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], devices[i].Address);
        }
        return buffer;
    }

    private static DeviceInfo Node(string id, bool isActive) => new()
    {
        Id = id,
        Name = "Test peripheral",
        Category = DeviceCategory.Bluetooth,
        IsActive = isActive,
    };

    // ── ParseDeviceInfoList ───────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsEveryEntrysAddressAndFlags()
    {
        var flags = BluetoothStackLinkState.ParseDeviceInfoList(
            DeviceList((Keyboard, Paired | Connected), (Headset, Paired)));

        Assert.NotNull(flags);
        Assert.Equal(2, flags.Count);
        Assert.Equal(Paired | Connected, flags[Keyboard]);
        Assert.Equal(Paired, flags[Headset]);
    }

    [Fact]
    public void Parse_EmptyList_IsEmptyNotNull()
    {
        var flags = BluetoothStackLinkState.ParseDeviceInfoList(DeviceList());

        Assert.NotNull(flags);
        Assert.Empty(flags);
    }

    [Fact]
    public void Parse_ResponseShorterThanItsCountNeeds_IsRejected()
    {
        var full = DeviceList((Keyboard, Connected), (Headset, Paired));
        var truncated = full.AsSpan(0, full.Length - 1);

        Assert.Null(BluetoothStackLinkState.ParseDeviceInfoList(truncated));
    }

    [Fact]
    public void Parse_NoHeader_IsRejected()
    {
        Assert.Null(BluetoothStackLinkState.ParseDeviceInfoList(new byte[3]));
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]   // the devnode still says active after a disconnect
    [InlineData(false, true)]   // and inactive after a connect
    public void Apply_BrEdrLinkNode_TakesActivityFromTheStack(bool devnodeActive, bool stackConnected)
    {
        var stack = new Dictionary<ulong, uint> { [Keyboard] = Paired | (stackConnected ? Connected : 0) };

        var device = BluetoothStackLinkState.Apply(Node(KeyboardLinkNode, devnodeActive), stack);

        Assert.Equal(stackConnected, device.IsActive);
        Assert.Equal(KeyboardLinkNode, device.Id.Value);
    }

    [Fact]
    public void Apply_LeLinkNode_KeepsTheDevnodeValue()
    {
        // BDIF_LE_CONNECTED is unmeasured, so an LE node is left on its devnode status.
        var stack = new Dictionary<ulong, uint> { [Mouse] = Paired };

        var device = BluetoothStackLinkState.Apply(Node(MouseLinkNode, isActive: true), stack);

        Assert.True(device.IsActive);
    }

    [Fact]
    public void Apply_ServiceNode_IsUntouched()
    {
        var stack = new Dictionary<ulong, uint> { [Keyboard] = Paired };

        var device = BluetoothStackLinkState.Apply(Node(KeyboardServiceNode, isActive: true), stack);

        Assert.True(device.IsActive);
    }

    [Fact]
    public void Apply_AddressTheStackDoesNotList_KeepsTheDevnodeValue()
    {
        var stack = new Dictionary<ulong, uint> { [Headset] = Paired | Connected };

        var device = BluetoothStackLinkState.Apply(Node(KeyboardLinkNode, isActive: true), stack);

        Assert.True(device.IsActive);
    }

    [Fact]
    public void Apply_NoStackAnswer_KeepsTheDevnodeValue()
    {
        var device = BluetoothStackLinkState.Apply(Node(KeyboardLinkNode, isActive: true), stackFlags: null);

        Assert.True(device.IsActive);
    }

    [Fact]
    public void Apply_BrEdrUsesClassicConnected_NotTheLeBit()
    {
        var stack = new Dictionary<ulong, uint> { [Keyboard] = Paired | LeConnected };

        var device = BluetoothStackLinkState.Apply(Node(KeyboardLinkNode, isActive: true), stack);

        Assert.False(device.IsActive);
    }

    // ── IsBrEdrLinkNode ───────────────────────────────────────────────────

    [Theory]
    [InlineData(KeyboardLinkNode, true)]
    [InlineData(KeyboardServiceNode, false)]
    [InlineData(MouseLinkNode, false)]
    [InlineData(@"USB\VID_046D&PID_C52B\5&3A1B2C&0&2", false)]
    public void IsBrEdrLinkNode_OnlyTheBthenumDevNode(string id, bool expected)
    {
        Assert.Equal(expected, BluetoothStackLinkState.IsBrEdrLinkNode(Node(id, isActive: true)));
    }
}
