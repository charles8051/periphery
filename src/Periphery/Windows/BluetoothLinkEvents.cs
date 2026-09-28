// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>The link type in <c>BTH_HCI_EVENT_INFO.connectionType</c> (<c>bthdef.h</c>).</summary>
internal enum BluetoothLinkType : byte
{
    /// <summary>A BR/EDR baseband link. It carries the peripheral's connection.</summary>
    Acl = 1,

    /// <summary>A synchronous audio link layered on an ACL link. It comes and goes with calls.</summary>
    Sco = 2,

    /// <summary>A Bluetooth Low Energy link.</summary>
    Le = 3,
}

/// <summary>One decoded <c>GUID_BLUETOOTH_HCI_EVENT</c>: a link to a remote device came up or went down.</summary>
internal readonly record struct BluetoothLinkChange(ulong Address, BluetoothLinkType Type, bool Connected);

/// <summary>
/// The pure half of Windows Bluetooth link events (issue #286). The Bluetooth driver raises
/// <c>GUID_BLUETOOTH_HCI_EVENT</c> on a handle to the local radio when a remote device's link
/// comes up or goes down; cfgmgr32's devnode stream raises nothing for the same change. This
/// decodes the event and decides which cached devnode it applies to. The provider owns the
/// registration, the cache and the raising.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BluetoothLinkEvents
{
    /// <summary><c>GUID_BLUETOOTH_HCI_EVENT</c> from <c>bthdef.h</c>.</summary>
    internal static readonly Guid HciEventGuid = new("fc240062-1541-49be-b463-84c4dcd7bf7f");

    // CM_NOTIFY_EVENT_DATA for a device-handle custom event:
    //   FilterType @0, Reserved @4, u.DeviceHandle { GUID EventGuid @8; LONG NameOffset @24;
    //   DWORD DataSize @28; BYTE Data[] @32 }
    // BTH_HCI_EVENT_INFO in Data: BTH_ADDR bthAddress @0; UCHAR connectionType @8; UCHAR connected @9.
    private const int EventGuidOffset = 8;
    private const int DataSizeOffset = 28;
    private const int DataOffset = 32;
    private const int HciEventInfoLength = 10;

    /// <summary>
    /// Decodes the <c>CM_NOTIFY_EVENT_DATA</c> of a <c>CM_NOTIFY_ACTION_DEVICECUSTOMEVENT</c>.
    /// Returns <see langword="false"/> for any other custom event, or for data too short to hold
    /// a <c>BTH_HCI_EVENT_INFO</c>.
    /// </summary>
    internal static bool TryDecode(ReadOnlySpan<byte> eventData, out BluetoothLinkChange change)
    {
        change = default;
        if (eventData.Length < DataOffset + HciEventInfoLength)
            return false;

        if (new Guid(eventData.Slice(EventGuidOffset, 16)) != HciEventGuid)
            return false;

        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(eventData[DataSizeOffset..]);
        if (dataSize < HciEventInfoLength)
            return false;

        var info = eventData[DataOffset..];
        change = new BluetoothLinkChange(
            Address: BinaryPrimitives.ReadUInt64LittleEndian(info),
            Type: (BluetoothLinkType)info[8],
            Connected: info[9] != 0);
        return true;
    }

    /// <summary>
    /// Reads the remote address from the devnode that carries a peripheral's link state, as
    /// <see cref="BluetoothAddress.TryParseInstanceId"/> does, and gives its transport as the
    /// link type an HCI event reports for it. Service and function nodes of the same peripheral
    /// return <see langword="false"/>.
    /// </summary>
    internal static bool TryGetLinkAddress(string instanceId, out ulong address, out BluetoothLinkType type)
    {
        address = 0;
        type = default;
        if (!BluetoothAddress.TryParseInstanceId(instanceId, out var parsed, out var transport))
            return false;

        address = parsed.Value;
        type = transport == BluetoothTransport.LowEnergy ? BluetoothLinkType.Le : BluetoothLinkType.Acl;
        return true;
    }

    /// <summary>
    /// Finds the link devnode a change applies to and returns it with <see cref="DeviceInfo.IsActive"/>
    /// taken from the event. Returns <see langword="null"/> for an SCO change, which is an audio
    /// sub-link and says nothing about the peripheral's connection, and when no known devnode
    /// carries the address on that transport.
    /// </summary>
    /// <remarks>
    /// The activity comes from the event, not from the devnode. Measured on a BR/EDR peripheral,
    /// the devnode still read active 28 ms after a disconnect event and inactive 2 s later.
    /// </remarks>
    internal static DeviceInfo? Resolve(IEnumerable<DeviceInfo> known, BluetoothLinkChange change)
    {
        if (change.Type is not (BluetoothLinkType.Acl or BluetoothLinkType.Le))
            return null;

        foreach (var device in known)
        {
            if (TryGetLinkAddress(device.Id.Value, out ulong address, out var type)
                && address == change.Address
                && type == change.Type)
            {
                return device with { IsActive = change.Connected };
            }
        }

        return null;
    }
}
