// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using InTheHand.Bluetooth;

namespace Periphery.Ble.InTheHand;

/// <summary>
/// Joins a Periphery <see cref="DeviceInfo"/> to 32feet's <see cref="BluetoothDevice"/>, for GATT
/// access (ADR-0085 D7).
/// </summary>
public static class DeviceInfoBleExtensions
{
    /// <summary>
    /// Resolves the 32feet <see cref="BluetoothDevice"/> for a Bluetooth LE peripheral, by its address.
    /// On Windows that is the LE link node, <c>BTHLE\DEV_&lt;address&gt;</c>. On Linux it is the bond
    /// Periphery reads from BlueZ, <c>bluez:&lt;adapter&gt;/&lt;device&gt;</c> (ADR-0091).
    /// </summary>
    /// <param name="device">The peripheral, as <c>OfCategory(DeviceCategory.Bluetooth)</c> enumerates it.</param>
    /// <param name="cancellationToken">Cancels the wait for 32feet's resolution.</param>
    /// <returns>
    /// The device, or <see langword="null"/> when 32feet cannot resolve the address. An unreachable
    /// peripheral is an ordinary outcome, not a fault.
    /// </returns>
    /// <remarks>
    /// <para>Resolving does not connect. Call <c>Gatt.ConnectAsync()</c> on the result.</para>
    /// <para>The join key is the peripheral's address, which is not an identity (ADR-0083). On
    /// Windows it holds across disconnects, peripheral and host reboots, and RPA rotation. A
    /// re-paired peripheral that uses private addresses comes back under a new node and a new
    /// address.</para>
    /// <para>On Linux, 32feet uses the first adapter BlueZ reports. A bond on another adapter
    /// resolves to <see langword="null"/>. A bond known to support only BR/EDR throws. One whose
    /// transport BlueZ does not reveal resolves (issue #302), and if it is classic-only, its
    /// <c>Gatt.ConnectAsync()</c> fails. See <see cref="DeviceInfo.BluetoothTransports"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="device"/> is not a peripheral this platform's 32feet provider can resolve, or
    /// it is known not to support Bluetooth LE.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The <c>net10.0</c> build was called off Linux, or BlueZ reports no adapter.
    /// </exception>
    public static async Task<BluetoothDevice?> ToBluetoothDeviceAsync(
        this DeviceInfo device, CancellationToken cancellationToken = default)
    {
        var (address, platform) = BleJoin.AddressOf(device);
#if WINDOWS
        if (platform != BleJoinPlatform.Windows)
            throw new ArgumentException($"'{device.Id.Value}' is a BlueZ bond. It resolves only on Linux.", nameof(device));
#else
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "This build of Periphery.Ble.InTheHand resolves BlueZ devices on Linux. On Windows, target net10.0-windows10.0.19041.0 or later.");
        }
        if (platform != BleJoinPlatform.Linux)
            throw new ArgumentException($"'{device.Id.Value}' is a Windows device node. It resolves only on Windows.", nameof(device));

        // 32feet's Linux FromIdAsync reads an adapter that only its Bluetooth calls initialise.
        await Bluetooth.GetAvailabilityAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
#endif
        string id = BleJoin.ToBluetoothDeviceId(address, platform);
        return await BluetoothDevice.FromIdAsync(id).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
