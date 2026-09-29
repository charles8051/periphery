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
    /// Resolves the 32feet <see cref="BluetoothDevice"/> for a Bluetooth LE link node,
    /// <c>BTHLE\DEV_&lt;address&gt;</c>, by the address in its instance id.
    /// </summary>
    /// <param name="device">An LE link node, as <c>OfCategory(DeviceCategory.Bluetooth)</c> enumerates it.</param>
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
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an LE link node.</exception>
    public static async Task<BluetoothDevice?> ToBluetoothDeviceAsync(
        this DeviceInfo device, CancellationToken cancellationToken = default)
    {
        string id = BleJoin.ToBluetoothDeviceId(BleJoin.LeAddressOf(device));
        return await BluetoothDevice.FromIdAsync(id).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
