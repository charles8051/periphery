// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Usb;

/// <summary>
/// Reconnect-resilient raw-USB handle. Opens a <see cref="UsbDevice"/> whenever
/// the tracked device becomes active and disposes it on disconnect, surfacing
/// <see cref="DeviceProxyBase{TDevice,TException}.Device"/> and
/// <see cref="DeviceProxyBase{TDevice,TException}.IsOpen"/> across the cycle.
/// </summary>
/// <remarks>
/// Mirrors <c>HidDeviceProxy</c>: the reconnect / lifecycle state machine lives
/// in the core <see cref="DeviceProxyBase{TDevice,TException}"/>; this leaf only
/// supplies the device-open hook plus the two factory entry points.
/// </remarks>
public sealed class UsbDeviceProxy : DeviceProxyBase<UsbDevice, UsbException>
{
    private UsbDeviceProxy(
        DeviceTracker tracker, DeviceWatcher watcher, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, watcher, recoveryPolicy) { }

    private UsbDeviceProxy(DeviceTracker tracker, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, recoveryPolicy) { }

    /// <inheritdoc />
    protected override Task<UsbDevice> OpenDeviceAsync(DeviceInfo deviceInfo, CancellationToken ct)
        => UsbDevice.OpenAsync(deviceInfo, ct);

    /// <summary>
    /// Creates a self-contained proxy that owns its watcher and starts tracking
    /// devices matching <paramref name="profile"/>.
    /// </summary>
    public static async Task<UsbDeviceProxy> OpenAsync(
        DeviceProfile profile,
        IRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new UsbDeviceProxy(tracker, watcher, recoveryPolicy);

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
    public static UsbDeviceProxy Create(
        DeviceTracker tracker,
        IRecoveryPolicy? recoveryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        var handle = new UsbDeviceProxy(tracker, recoveryPolicy);
        handle.CheckInitialState();
        return handle;
    }
}
