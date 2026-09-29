// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;

namespace Periphery.Ble.InTheHand;

/// <summary>
/// The pure half of the ADR-0085 D7 join: which address a <see cref="DeviceInfo"/> carries, and the
/// id 32feet resolves it by. <see cref="DeviceInfoBleExtensions"/> performs the resolution.
/// </summary>
internal static class BleJoin
{
    /// <summary>
    /// Reads the address from a Bluetooth LE link node, <c>BTHLE\DEV_&lt;address&gt;</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The node is not an LE link node.</exception>
    internal static BluetoothAddress LeAddressOf(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!BluetoothAddress.TryParseInstanceId(device.Id.Value, out var address, out var transport)
            || transport != BluetoothTransport.LowEnergy)
        {
            throw new ArgumentException(
                $"'{device.Id.Value}' is not a Bluetooth LE link node. Only a BTHLE\\DEV_<address> node " +
                "carries the address the join needs; service and function nodes of the same device do not.",
                nameof(device));
        }

        return address;
    }

    /// <summary>
    /// The id 32feet's <c>BluetoothDevice.FromIdAsync</c> takes for <paramref name="address"/>.
    /// </summary>
    /// <remarks>
    /// The format is 32feet's per platform: hex digits on Windows, where it parses the id as a
    /// number, and colon-separated octets on Linux, where it is BlueZ's <c>Device1.Address</c>.
    /// Twelve digits are always written. 32feet's own Windows ids drop leading zeros, so compare
    /// them as parsed <see cref="BluetoothAddress"/> values, never as strings.
    /// </remarks>
    internal static string ToBluetoothDeviceId(BluetoothAddress address, bool windows) =>
        windows ? address.ToString("X12", CultureInfo.InvariantCulture) : address.ToString();
}
