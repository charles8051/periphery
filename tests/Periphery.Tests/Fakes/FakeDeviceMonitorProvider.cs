namespace Periphery.Tests;

/// <summary>
/// Fake device monitor provider for testing event subscriptions.
/// Only handles real-time events — initial snapshot is done by DeviceWatcher
/// via FakeDeviceProvider.
/// </summary>
internal class FakeDeviceMonitorProvider : IDeviceMonitorProvider
{
    private bool _started;

    public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
    public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;

    public Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
    {
        if (_started)
            throw new InvalidOperationException("Already started");

        _started = true;
        return Task.CompletedTask;
    }

    public void SimulateConnect(DeviceInfo device)
    {
        if (!_started)
            throw new InvalidOperationException("Not started");

        DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device));
        if (device.IsActive)
            DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    public void SimulateDisconnect(DeviceInfo device)
    {
        if (!_started)
            throw new InvalidOperationException("Not started");

        DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    /// <summary>
    /// Simulates a devnode status transition (e.g. Bluetooth device coming
    /// into range or going out of range without pairing/unpairing).
    /// Fires <see cref="DeviceActivated"/> or <see cref="DeviceDeactivated"/>
    /// based on the device's <see cref="DeviceInfo.IsActive"/> stat
    /// </summary>
    public void SimulateStatusChange(DeviceInfo device)
    {
        if (!_started)
            throw new InvalidOperationException("Not started");

        if (device.IsActive)
            DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device));
        else
            DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    /// <summary>
    /// Simulates a property change on an existing device (e.g. battery level
    /// dropping, network link speed changing). Fires <see cref="DevicePropertyChanged"/>
    /// with <paramref name="previous"/> and <paramref name="current"/> snapshots.
    /// </summary>
    public void SimulatePropertyChange(DeviceInfo previous, DeviceInfo current)
    {
        if (!_started)
            throw new InvalidOperationException("Not started");

        DevicePropertyChanged?.Invoke(this, new DeviceModificationEventArgs(previous, current));
    }

    public ValueTask DisposeAsync()
    {
        _started = false;
        return ValueTask.CompletedTask;
    }
}
