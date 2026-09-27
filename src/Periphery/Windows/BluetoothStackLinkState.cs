// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// The pure half of reading Bluetooth link state from the stack rather than the devnode
/// (issue #288). A BR/EDR link devnode's status lags its link: measured, it still read active
/// 28 ms after a disconnect and inactive 2 s later. The stack's own device list
/// (<c>IOCTL_BTH_GET_DEVICE_INFO</c>) had <c>BDIF_CONNECTED</c> matching the link 0-3 ms after
/// every change. This parses that list and applies it to a link devnode's payload.
/// <see cref="BluetoothRadio"/> performs the IOCTL.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BluetoothStackLinkState
{
    /// <summary><c>BDIF_CONNECTED</c> from <c>bthdef.h</c>.</summary>
    internal const uint BDIF_CONNECTED = 0x00000020;

    // BTH_DEVICE_INFO_LIST is byte-packed (bthioctl.h): ULONG numOfDevices @0, then
    // BTH_DEVICE_INFO deviceList[] @4. BTH_DEVICE_INFO keeps natural alignment: ULONG flags @0,
    // BTH_ADDR address @8, BTH_COD classOfDevice @16, CHAR name[248] @20; 272 bytes.
    internal const int ListHeaderSize = 4;
    internal const int DeviceInfoSize = 272;
    private const int AddressOffset = 8;

    /// <summary>
    /// Parses a <c>BTH_DEVICE_INFO_LIST</c> response into address → <c>BDIF_*</c> flags. Returns
    /// <see langword="null"/> when the response is shorter than its own device count needs.
    /// </summary>
    internal static Dictionary<ulong, uint>? ParseDeviceInfoList(ReadOnlySpan<byte> response)
    {
        if (response.Length < ListHeaderSize)
            return null;

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(response);
        if ((ulong)response.Length < ListHeaderSize + (ulong)count * DeviceInfoSize)
            return null;

        var flagsByAddress = new Dictionary<ulong, uint>((int)count);
        for (int i = 0; i < count; i++)
        {
            var entry = response.Slice(ListHeaderSize + i * DeviceInfoSize, DeviceInfoSize);
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(entry[AddressOffset..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            flagsByAddress[address] = flagsByAddress.GetValueOrDefault(address) | flags;
        }
        return flagsByAddress;
    }

    /// <summary>
    /// Whether <paramref name="device"/> is a BR/EDR link devnode (<c>BTHENUM\DEV_&lt;address&gt;</c>),
    /// the only node this applies to.
    /// </summary>
    internal static bool IsBrEdrLinkNode(DeviceInfo device) =>
        BluetoothLinkEvents.TryGetLinkAddress(device.Id.Value, out _, out var type)
        && type == BluetoothLinkType.Acl;

    /// <summary>
    /// Returns <paramref name="device"/> with <see cref="DeviceInfo.IsActive"/> taken from the
    /// stack's <c>BDIF_CONNECTED</c>, when it is a BR/EDR link devnode and the stack lists its
    /// address. Anything else is returned unchanged, including LE link devnodes: whether
    /// <c>BDIF_LE_CONNECTED</c> tracks the link as promptly is unmeasured.
    /// </summary>
    internal static DeviceInfo Apply(DeviceInfo device, IReadOnlyDictionary<ulong, uint>? stackFlags)
    {
        if (stackFlags is null
            || !BluetoothLinkEvents.TryGetLinkAddress(device.Id.Value, out ulong address, out var type)
            || type != BluetoothLinkType.Acl
            || !stackFlags.TryGetValue(address, out uint flags))
        {
            return device;
        }

        bool connected = (flags & BDIF_CONNECTED) != 0;
        return device.IsActive == connected ? device : device with { IsActive = connected };
    }
}
