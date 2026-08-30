// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Implemented by each platform back-end to enumerate devices.
/// Implement this interface to inject a fake or in-memory device list in tests
/// (pass your implementation to <see cref="DeviceQuery(IDeviceProvider)"/> or
/// <see cref="DeviceWatcher(IDeviceProvider, IDeviceMonitorProvider)"/>).
/// <para>
/// The provider receives a <see cref="DeviceFilter"/> and may inspect
/// its structured properties (category, name, manufacturer, USB IDs)
/// to narrow the OS-level query as a performance hint. However,
/// <see cref="DeviceFilter.Matches"/> is always re-evaluated in-memory
/// by the caller — correctness never depends on provider cooperation.
/// </para>
/// </summary>
public interface IDeviceProvider
{
    /// <summary>Stream devices matching <paramref name="filter"/>.</summary>
    IAsyncEnumerable<DeviceInfo> EnumerateAsync(
        DeviceFilter filter,
        CancellationToken ct = default);
}

/// <summary>
/// Provides the previous and current <see cref="DeviceInfo"/> snapshots when
/// a platform provider detects that a device's properties have changed.
/// Used by <see cref="IDeviceMonitorProvider.DevicePropertyChanged"/> and
/// processed by <see cref="DeviceWatcher"/> to compute
/// <see cref="DevicePropertyChangedEventArgs.ChangedProperties"/> via
/// <see cref="DeviceInfoDiff"/>.
/// </summary>
public sealed class DeviceModificationEventArgs(DeviceInfo previous, DeviceInfo current) : EventArgs
{
    public DeviceInfo Previous { get; } = previous;
    public DeviceInfo Current { get; } = current;
}

/// <summary>
/// Implemented by each platform back-end to monitor device lifecycle.
/// Implement this interface to simulate device events in tests
/// (pass your implementation to <see cref="DeviceWatcher(IDeviceProvider, IDeviceMonitorProvider)"/>).
/// <para>Raises four events covering two orthogonal state transitions:</para>
/// <list type="bullet">
/// <item><see cref="DeviceAppeared"/>/<see cref="DeviceDisappeared"/> —
/// the device entered or left the OS device tree (install, pair, uninstall, unpair).</item>
/// <item><see cref="DeviceActivated"/>/<see cref="DeviceDeactivated"/> —
/// the device became physically active or inactive (driver started/stopped,
/// Bluetooth in/out of range).</item>
/// </list>
/// </summary>
public interface IDeviceMonitorProvider : IAsyncDisposable
{
    Task StartAsync(DeviceFilter filter, CancellationToken ct = default);

    /// <summary>A new device entered the OS device tree.</summary>
    event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;

    /// <summary>A device left the OS device tree.</summary>
    event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;

    /// <summary>A device became physically active (driver started, hardware present).</summary>
    event EventHandler<DeviceChangeEventArgs>? DeviceActivated;

    /// <summary>A device became physically inactive (driver stopped, hardware removed).</summary>
    event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;

    /// <summary>
    /// One or more properties on an existing device changed value.
    /// Provides both the previous and current <see cref="DeviceInfo"/> snapshots.
    /// Fired from modification event callbacks (<c>CM_Register_Notification</c>
    /// on Windows, UPower D-Bus on Linux, IOKit on macOS).
    /// </summary>
    event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;
}

/// <summary>
/// Resolves the correct provider for the current OS at runtime.
/// </summary>
internal static class DeviceProviderFactory
{
    internal static IDeviceProvider GetProvider(Windows.WindowsProviderOptions? options = null)
    {
        if (OperatingSystem.IsWindows()) return new Windows.WindowsDeviceProvider(options);
        if (OperatingSystem.IsLinux())   return new Linux.LinuxDeviceProvider();
        if (OperatingSystem.IsMacOS())   return new MacOS.MacOSDeviceProvider();
        throw new PlatformNotSupportedException();
    }

    internal static IDeviceMonitorProvider GetMonitorProvider()
    {
        if (OperatingSystem.IsWindows()) return new Windows.WindowsDeviceMonitorProvider();
        if (OperatingSystem.IsLinux())   return new Linux.LinuxDeviceMonitorProvider();
        if (OperatingSystem.IsMacOS())   return new MacOS.MacOSDeviceMonitorProvider();
        throw new PlatformNotSupportedException();
    }
}
