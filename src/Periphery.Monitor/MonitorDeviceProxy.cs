// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor;

/// <summary>
/// Reconnect-resilient monitor-control handle. Opens a
/// <see cref="MonitorDevice"/> whenever the tracked monitor becomes active
/// and disposes it on disconnect, surfacing
/// <see cref="DeviceProxyBase{TDevice,TException}.Device"/> and
/// <see cref="DeviceProxyBase{TDevice,TException}.IsOpen"/> across the cycle.
/// </summary>
/// <remarks>
/// Mirrors <c>UsbDeviceProxy</c> / <c>HidDeviceProxy</c>: the reconnect /
/// lifecycle state machine lives in the core
/// <see cref="DeviceProxyBase{TDevice,TException}"/>; this leaf only supplies
/// the device-open hook plus the two factory entry points. Layer 2 earns its
/// keep here — monitors hot-unplug routinely and DDC goes dark while a panel
/// sleeps, so consumers like a night-dimming loop live through reconnects
/// constantly (ADR-0058 D2/D10).
/// </remarks>
public sealed class MonitorDeviceProxy : DeviceProxyBase<MonitorDevice, MonitorException>
{
    private MonitorDeviceProxy(
        DeviceTracker tracker, DeviceWatcher watcher, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, watcher, recoveryPolicy) { }

    private MonitorDeviceProxy(DeviceTracker tracker, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, recoveryPolicy) { }

    /// <inheritdoc />
    protected override Task<MonitorDevice> OpenDeviceAsync(DeviceInfo deviceInfo, CancellationToken ct)
        => MonitorDevice.OpenAsync(deviceInfo, ct);

    /// <summary>
    /// Creates a self-contained proxy that owns its watcher and starts tracking
    /// monitors matching <paramref name="profile"/>.
    /// </summary>
    public static async Task<MonitorDeviceProxy> OpenAsync(
        DeviceProfile profile,
        IRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new MonitorDeviceProxy(tracker, watcher, recoveryPolicy);

        try
        {
            await watcher.StartAsync(ct).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a proxy that borrows a caller-owned <paramref name="tracker"/>
    /// already attached to a running watcher.
    /// </summary>
    public static MonitorDeviceProxy Create(
        DeviceTracker tracker,
        IRecoveryPolicy? recoveryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        var handle = new MonitorDeviceProxy(tracker, recoveryPolicy);
        handle.CheckInitialState();
        return handle;
    }
}
