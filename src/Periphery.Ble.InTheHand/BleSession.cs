// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using InTheHand.Bluetooth;

namespace Periphery.Ble.InTheHand;

/// <summary>
/// One connected GATT session, as <see cref="BleDeviceProxy"/> opens it. Valid from
/// <c>DeviceOpened</c> until <c>DeviceClosed</c>; disposing it disconnects.
/// </summary>
public sealed class BleSession : IAsyncDisposable
{
    private readonly TaskCompletionSource _linkDropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal BleSession(BluetoothDevice device)
    {
        Device = device;
        Device.GattServerDisconnected += OnGattServerDisconnected;
    }

    /// <summary>The 32feet device.</summary>
    public BluetoothDevice Device { get; }

    /// <summary>The device's GATT server, connected while the session is open.</summary>
    public RemoteGattServer Gatt => Device.Gatt;

    /// <summary>
    /// Completes when the link drops, or never, and throws <see cref="BleException"/> then.
    /// 32feet also raises <c>GattServerDisconnected</c> for a disconnect the session makes itself,
    /// so one raised after disposal begins is not a drop.
    /// </summary>
    internal async Task WaitForLinkDropAsync(CancellationToken ct)
    {
        await _linkDropped.Task.WaitAsync(ct).ConfigureAwait(false);
        throw new BleException($"The link to '{Device.Id}' dropped.");
    }

    private void OnGattServerDisconnected(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _linkDropped.TrySetResult();
    }

    /// <summary>Disconnects the GATT server.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        Device.GattServerDisconnected -= OnGattServerDisconnected;
        Gatt.Disconnect();
        return ValueTask.CompletedTask;
    }
}
