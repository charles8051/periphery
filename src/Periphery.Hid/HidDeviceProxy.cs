// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid;

/// <summary>
/// Layer 2 reconnect-resilient HID device handle. Composes a <see cref="DeviceTracker"/>
/// internally and automatically opens a <see cref="HidDevice"/> whenever the tracked
/// device becomes connected, closing it when the device disconnects.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an instance via <see cref="OpenAsync(DeviceProfile, IRecoveryPolicy?, CancellationToken)"/>.
/// The factory creates and wires up the <see cref="DeviceTracker"/> and
/// <see cref="DeviceWatcher"/> so the caller never needs to manage them directly.
/// </para>
/// <para>
/// <b>I/O model:</b>
/// Use <see cref="DeviceProxyBase{TDevice,TException}.DeviceOpened"/> to receive
/// the <see cref="HidDevice"/> when a connection is established. The device argument
/// is valid for exactly as long as the connection lasts.
/// </para>
/// <para>
/// <b>State semantics:</b>
/// <list type="bullet">
/// <item>
///   <see cref="DeviceProxyBase{TDevice,TException}.IsOpen"/> is
///   <see langword="true"/> when the platform file handle is open and ready for I/O.
///   This is distinct from <see cref="DeviceInfo.IsActive"/>, which is an
///   OS-enumeration snapshot indicating whether the driver is started.
/// </item>
/// <item>
///   The handle transitions <c>false → true</c> when
///   <see cref="DeviceProxyBase{TDevice,TException}.DeviceOpened"/> fires and
///   <c>true → false</c> when
///   <see cref="DeviceProxyBase{TDevice,TException}.DeviceClosed"/> fires.
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Threading:</b>
/// <see cref="DeviceProxyBase{TDevice,TException}.DeviceOpened"/> and
/// <see cref="DeviceProxyBase{TDevice,TException}.DeviceClosed"/> fire on
/// thread-pool threads. UI dispatch is the consumer's responsibility.
/// </para>
/// </remarks>
public sealed class HidDeviceProxy
    : DeviceProxyBase<HidDevice, HidException>
{
    private HidDeviceProxy(
        DeviceTracker tracker, DeviceWatcher watcher, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, watcher, recoveryPolicy) { }

    private HidDeviceProxy(DeviceTracker tracker, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, recoveryPolicy) { }

    /// <inheritdoc/>
    protected override Task<HidDevice> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct)
        => HidDevice.OpenAsync(deviceInfo, ct);

    /// <summary>
    /// Creates a <see cref="HidDeviceProxy"/> that tracks devices matching
    /// <paramref name="profile"/> and starts the underlying <see cref="DeviceWatcher"/>.
    /// </summary>
    /// <param name="profile">
    /// The device profile describing which HID device to track. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional reconnect-cadence/give-up policy. Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when omitted.
    /// </param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    /// <returns>A started <see cref="HidDeviceProxy"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="profile"/> is <see langword="null"/>.
    /// </exception>
    public static async Task<HidDeviceProxy> OpenAsync(
        DeviceProfile profile,
        IRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new HidDeviceProxy(tracker, watcher, recoveryPolicy);

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
    /// Creates a <see cref="HidDeviceProxy"/> that borrows an existing
    /// <see cref="DeviceTracker"/>. The caller is responsible for managing
    /// the <see cref="DeviceWatcher"/> lifetime. Use this when a single
    /// watcher powers multiple HID devices to reduce system calls.
    /// </summary>
    /// <param name="tracker">
    /// An existing tracker, already attached to a running
    /// <see cref="DeviceWatcher"/>. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional reconnect-cadence/give-up policy. Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when omitted.
    /// </param>
    /// <returns>A <see cref="HidDeviceProxy"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tracker"/> is <see langword="null"/>.
    /// </exception>
    public static HidDeviceProxy Create(
        DeviceTracker tracker,
        IRecoveryPolicy? recoveryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        var handle = new HidDeviceProxy(tracker, recoveryPolicy);
        handle.CheckInitialState();
        return handle;
    }
}
