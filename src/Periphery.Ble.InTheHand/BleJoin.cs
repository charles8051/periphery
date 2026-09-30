// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;

namespace Periphery.Ble.InTheHand;

/// <summary>Whose 32feet provider resolves a device, which decides the id it takes.</summary>
internal enum BleJoinPlatform
{
    /// <summary>A Windows LE link node, <c>BTHLE\DEV_&lt;address&gt;</c>.</summary>
    Windows,

    /// <summary>A bond Periphery read from BlueZ, <c>bluez:&lt;adapter&gt;/&lt;device&gt;</c> (ADR-0091).</summary>
    Linux,
}

/// <summary>
/// The pure half of the ADR-0085 D7 join: which address a <see cref="DeviceInfo"/> carries, and the
/// id 32feet resolves it by. <see cref="DeviceInfoBleExtensions"/> performs the resolution.
/// </summary>
internal static class BleJoin
{
    /// <summary>The prefix of a BlueZ bond's <see cref="DeviceInfo.Id"/>, as ADR-0091 D4 writes it.</summary>
    internal const string BlueZIdPrefix = "bluez:";

    /// <summary>
    /// Reads the peripheral's address, and whose 32feet resolves it. A Windows LE link node carries
    /// the address in its instance id. A BlueZ bond carries it in <see cref="DeviceInfo.MacAddress"/>.
    /// </summary>
    /// <remarks>
    /// A BlueZ bond known not to support LE is refused. One whose transport is not known is
    /// accepted, since <c>Device1</c> merges both bearers (issue #302). A Windows node must be the LE
    /// one.
    /// </remarks>
    /// <exception cref="ArgumentException">The device is neither.</exception>
    internal static (BluetoothAddress Address, BleJoinPlatform Platform) AddressOf(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (BluetoothAddress.TryParseInstanceId(device.Id.Value, out var address, out var transport)
            && transport == BluetoothTransport.LowEnergy)
            return (address, BleJoinPlatform.Windows);

        if (device.Id.Value.StartsWith(BlueZIdPrefix, StringComparison.Ordinal)
            && device.MacAddress?.GetAddressBytes() is { Length: 6 } bytes)
        {
            // Known and without LE, it has no GATT to reach. Unknown is let through (issue #302).
            if (device.BluetoothTransports is { } known and not BluetoothTransports.None
                && !known.HasFlag(BluetoothTransports.LowEnergy))
            {
                throw new ArgumentException(
                    $"'{device.Id.Value}' is known to support only {known}, not Bluetooth LE.", nameof(device));
            }

            ulong value = 0;
            foreach (byte b in bytes)
                value = (value << 8) | b;
            return (new BluetoothAddress(value), BleJoinPlatform.Linux);
        }

        throw new ArgumentException(
            $"'{device.Id.Value}' is neither a Windows Bluetooth LE link node, BTHLE\\DEV_<address>, nor a " +
            "BlueZ bond, bluez:<adapter>/<device>. Service and function nodes of a device do not carry the " +
            "address the join needs.",
            nameof(device));
    }

    /// <summary>
    /// The id 32feet's <c>BluetoothDevice.FromIdAsync</c> takes for <paramref name="address"/> on
    /// <paramref name="platform"/>. Windows parses twelve hex digits as a number. Linux matches BlueZ's
    /// colon form.
    /// </summary>
    /// <remarks>
    /// 32feet's own Windows ids drop leading zeros, so compare them as parsed
    /// <see cref="BluetoothAddress"/> values, never as strings.
    /// </remarks>
    internal static string ToBluetoothDeviceId(BluetoothAddress address, BleJoinPlatform platform) =>
        platform == BleJoinPlatform.Windows
            ? address.ToString("X12", CultureInfo.InvariantCulture)
            : address.ToString();
}
