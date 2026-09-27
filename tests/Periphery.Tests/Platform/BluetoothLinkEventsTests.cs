using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="BluetoothLinkEvents"/>, the pure half of Windows Bluetooth link
/// events (issue #286): decoding <c>GUID_BLUETOOTH_HCI_EVENT</c> from <c>CM_NOTIFY_EVENT_DATA</c>
/// bytes, and choosing the devnode a link change applies to. Byte buffers and records only, so
/// they run anywhere. Addresses are synthetic.
/// </summary>
[SupportedOSPlatform("windows")]
public class BluetoothLinkEventsTests
{
    private const ulong Keyboard = 0xA0B1C2D3E4F5;
    private const ulong Mouse = 0xC1D2E3F4A5B6;

    private const string KeyboardLinkNode = @"BTHENUM\DEV_A0B1C2D3E4F5\7&2A1B3C4D&0&BLUETOOTHDEVICE_A0B1C2D3E4F5";
    private const string KeyboardServiceNode = @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B35B\7&2A1B3C4D&0&A0B1C2D3E4F5_C00000000";
    private const string KeyboardFunctionNode = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B35B&COL01\8&1F2E3D4C&0&0000";
    private const string MouseLinkNode = @"BTHLE\DEV_C1D2E3F4A5B6\7&11111111&0&C1D2E3F4A5B6";

    private static readonly Guid L2capEventGuid = new("7eae4030-b709-4aa8-ac55-e953829c9daa");

    // CM_NOTIFY_EVENT_DATA for a device-handle custom event: FilterType @0, Reserved @4,
    // EventGuid @8, NameOffset @24, DataSize @28, Data @32.
    private static byte[] CustomEvent(Guid eventGuid, ReadOnlySpan<byte> data, int? declaredSize = null)
    {
        var buffer = new byte[32 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 1); // CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE
        eventGuid.TryWriteBytes(buffer.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), -1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), declaredSize ?? data.Length);
        data.CopyTo(buffer.AsSpan(32));
        return buffer;
    }

    // BTH_HCI_EVENT_INFO: bthAddress @0, connectionType @8, connected @9, padded to 16.
    private static byte[] HciEvent(ulong address, byte connectionType, bool connected)
    {
        var info = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(info, address);
        info[8] = connectionType;
        info[9] = connected ? (byte)1 : (byte)0;
        return CustomEvent(BluetoothLinkEvents.HciEventGuid, info);
    }

    private static DeviceInfo Node(string id, bool isActive, DeviceCategory category = DeviceCategory.Bluetooth) => new()
    {
        Id = id,
        Name = "Test peripheral",
        Category = category,
        IsActive = isActive,
    };

    private static readonly DeviceInfo[] Tree =
    [
        Node(KeyboardServiceNode, isActive: true, DeviceCategory.Hid),
        Node(KeyboardFunctionNode, isActive: true, DeviceCategory.Keyboard),
        Node(KeyboardLinkNode, isActive: true),
        Node(MouseLinkNode, isActive: false),
        Node(@"USB\VID_046D&PID_C52B\5&3A1B2C&0&2", isActive: true, DeviceCategory.Usb),
    ];

    // ── TryDecode ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryDecode_HciEvent_ReadsAddressTypeAndState(bool connected)
    {
        Assert.True(BluetoothLinkEvents.TryDecode(HciEvent(Keyboard, 1, connected), out var change));

        Assert.Equal(new BluetoothLinkChange(Keyboard, BluetoothLinkType.Acl, connected), change);
    }

    [Fact]
    public void TryDecode_OtherCustomEvent_IsNotALinkChange()
    {
        var l2cap = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(l2cap, Keyboard);
        l2cap[10] = 1;

        Assert.False(BluetoothLinkEvents.TryDecode(CustomEvent(L2capEventGuid, l2cap), out _));
    }

    [Fact]
    public void TryDecode_DeclaredDataShorterThanHciEventInfo_IsRejected()
    {
        var data = HciEvent(Keyboard, 1, connected: true);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 9);

        Assert.False(BluetoothLinkEvents.TryDecode(data, out _));
    }

    [Fact]
    public void TryDecode_BufferTooShortForHciEventInfo_IsRejected()
    {
        var truncated = HciEvent(Keyboard, 1, connected: true).AsSpan(0, 32 + 9);

        Assert.False(BluetoothLinkEvents.TryDecode(truncated, out _));
    }

    // ── TryGetLinkAddress ─────────────────────────────────────────────────

    [Theory]
    [InlineData(KeyboardLinkNode, Keyboard, (byte)BluetoothLinkType.Acl)]
    [InlineData(MouseLinkNode, Mouse, (byte)BluetoothLinkType.Le)]
    [InlineData(@"bthenum\dev_a0b1c2d3e4f5\7&2a1b3c4d&0&bluetoothdevice_a0b1c2d3e4f5", Keyboard, (byte)BluetoothLinkType.Acl)]
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5", Keyboard, (byte)BluetoothLinkType.Acl)]
    public void TryGetLinkAddress_LinkNode_ReturnsAddressAndTransport(string instanceId, ulong address, byte type)
    {
        Assert.True(BluetoothLinkEvents.TryGetLinkAddress(instanceId, out ulong parsed, out var parsedType));
        Assert.Equal(address, parsed);
        Assert.Equal((BluetoothLinkType)type, parsedType);
    }

    [Theory]
    [InlineData(KeyboardServiceNode)]
    [InlineData(KeyboardFunctionNode)]
    [InlineData(@"USB\VID_046D&PID_C52B\5&3A1B2C&0&2")]
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F\7&0&0")]      // 11 hex digits
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4F5A\7&0&0")]    // 13 hex digits
    [InlineData(@"BTHENUM\DEV_A0B1C2D3E4G5\7&0&0")]     // not hex
    [InlineData(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_C1D2E3F4A5B6\8&0&0001")]
    public void TryGetLinkAddress_AnyOtherNode_IsNotALinkNode(string instanceId)
    {
        Assert.False(BluetoothLinkEvents.TryGetLinkAddress(instanceId, out _, out _));
    }

    // ── Resolve ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_AclChange_UpdatesTheBrEdrLinkNode_WithActivityFromTheEvent(bool connected)
    {
        var device = BluetoothLinkEvents.Resolve(Tree, new BluetoothLinkChange(Keyboard, BluetoothLinkType.Acl, connected));

        Assert.NotNull(device);
        Assert.Equal(KeyboardLinkNode, device.Id.Value);
        Assert.Equal(connected, device.IsActive);
        Assert.Equal(DeviceCategory.Bluetooth, device.Category);
    }

    [Fact]
    public void Resolve_LeChange_UpdatesTheLeLinkNode()
    {
        var device = BluetoothLinkEvents.Resolve(Tree, new BluetoothLinkChange(Mouse, BluetoothLinkType.Le, Connected: true));

        Assert.NotNull(device);
        Assert.Equal(MouseLinkNode, device.Id.Value);
        Assert.True(device.IsActive);
    }

    [Fact]
    public void Resolve_ScoChange_IsIgnored()
    {
        // SCO is a call's audio link on top of the ACL link. It dropping says nothing about
        // whether the headset is still connected.
        Assert.Null(BluetoothLinkEvents.Resolve(Tree, new BluetoothLinkChange(Keyboard, BluetoothLinkType.Sco, Connected: false)));
    }

    [Fact]
    public void Resolve_OnlyMatchesTheLinkNodeOfTheEventsTransport()
    {
        // The keyboard's address is on a BR/EDR node, so an LE change for it matches nothing,
        // and neither does an ACL change for the LE mouse.
        Assert.Null(BluetoothLinkEvents.Resolve(Tree, new BluetoothLinkChange(Keyboard, BluetoothLinkType.Le, Connected: true)));
        Assert.Null(BluetoothLinkEvents.Resolve(Tree, new BluetoothLinkChange(Mouse, BluetoothLinkType.Acl, Connected: true)));
    }

    [Fact]
    public void Resolve_UnknownAddress_MatchesNothing()
    {
        Assert.Null(BluetoothLinkEvents.Resolve(Tree, new BluetoothLinkChange(0x001122334455, BluetoothLinkType.Acl, Connected: true)));
    }

    [Fact]
    public void Resolve_NeverPicksTheServiceNode_ThatAlsoCarriesTheAddress()
    {
        // The service node's instance id embeds the same address, but it does not track the link.
        var serviceOnly = new[] { Node(KeyboardServiceNode, isActive: true, DeviceCategory.Hid) };

        Assert.Null(BluetoothLinkEvents.Resolve(serviceOnly, new BluetoothLinkChange(Keyboard, BluetoothLinkType.Acl, Connected: false)));
    }
}
