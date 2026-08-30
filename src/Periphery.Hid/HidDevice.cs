// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid;

/// <summary>
/// Layer 1 HID I/O primitive. Represents an open platform handle to a HID device
/// and exposes the four-method HID transfer surface.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an instance via <see cref="OpenAsync(DeviceInfo, CancellationToken)"/>, which
/// crosses the discovery/I/O boundary by opening the OS-level file handle.
/// </para>
/// <para>
/// For reconnect-resilient, lifecycle-managed access use <see cref="HidDeviceProxy"/>
/// instead. <see cref="HidDevice"/> is the right choice when you already have a
/// <see cref="DeviceInfo"/> and want a one-shot open.
/// </para>
/// <para>
/// <b>Windows:</b> access is shared (FILE_SHARE_READ | FILE_SHARE_WRITE). Some device
/// classes (keyboards, mice) are restricted by the OS; <see cref="OpenAsync"/> will
/// throw <see cref="System.IO.IOException"/> with an ACCESS_DENIED message for those
/// devices unless the caller is elevated.
/// </para>
/// <para>
/// <b>Linux:</b> the <c>/dev/hidrawN</c> node requires either <c>root</c> or a udev
/// rule granting read/write permissions.
/// </para>
/// </remarks>
public sealed class HidDevice : IAsyncDisposable
{
    private readonly IHidBackend _backend;
    private bool _disposed;

    private HidDevice(DeviceInfo deviceInfo, IHidBackend backend)
    {
        DeviceInfo = deviceInfo;
        _backend = backend;
    }

    // -----------------------------------------------------------------------
    // Discovery context
    // -----------------------------------------------------------------------

    /// <summary>The enumeration snapshot from which this device was opened.</summary>
    public DeviceInfo DeviceInfo { get; }

    // -----------------------------------------------------------------------
    // OS-enumerable metadata (populated without opening; cached from open-time caps)
    // -----------------------------------------------------------------------

    /// <summary>HID usage page (e.g. 0x0001 = Generic Desktop).</summary>
    public ushort UsagePage => _backend.UsagePage;

    /// <summary>HID usage within the usage page (e.g. 0x0005 = Gamepad).</summary>
    public ushort Usage => _backend.Usage;

    /// <summary>Maximum input report payload length, excluding the report ID byte.</summary>
    public int MaxInputReportLength => _backend.MaxInputReportLength;

    /// <summary>Maximum output report payload length, excluding the report ID byte.</summary>
    public int MaxOutputReportLength => _backend.MaxOutputReportLength;

    /// <summary>Maximum feature report payload length, excluding the report ID byte.</summary>
    public int MaxFeatureReportLength => _backend.MaxFeatureReportLength;

    // -----------------------------------------------------------------------
    // Transfer surface
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the next input report from the device.
    /// Blocks until a report is available or <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The received report.</returns>
    /// <exception cref="HidTransferException">
    /// Thrown if the device is disconnected mid-read or the OS returns an I/O error.
    /// </exception>
    public Task<HidReport> ReadReportAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.ReadReportAsync(ct);
    }

    /// <summary>
    /// Sends an output report to the device.
    /// </summary>
    /// <param name="report">The report to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="HidTransferException">
    /// Thrown if the write fails or the device is disconnected.
    /// </exception>
    public Task WriteReportAsync(HidReport report, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.WriteReportAsync(report, ct);
    }

    /// <summary>
    /// Reads a HID feature report from the device. Distinct from
    /// <see cref="ReadReportAsync"/>: feature reports are the request/response
    /// control-plane channel of HID, used for status queries (battery state,
    /// configuration, calibration) and vendor-defined protocols like Megatec Q1
    /// that ride feature report 0 with ASCII payloads.
    /// </summary>
    /// <param name="reportId">
    /// The report ID to request. <c>0</c> when the device exposes a single
    /// unnamed feature report; otherwise the descriptor-declared ID.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The feature report — its ID byte and payload.</returns>
    /// <exception cref="HidTransferException">
    /// Thrown if the device is disconnected, locked by a vendor driver,
    /// or the requested report ID isn't supported by this device.
    /// </exception>
    /// <exception cref="HidException">
    /// Thrown if the device doesn't advertise any feature reports at all
    /// (<see cref="MaxFeatureReportLength"/> is 0).
    /// </exception>
    public Task<HidReport> ReadFeatureReportAsync(byte reportId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.ReadFeatureReportAsync(reportId, ct);
    }

    /// <summary>
    /// Sends a HID feature report to the device. The request/response
    /// counterpart of <see cref="ReadFeatureReportAsync"/>. Used to issue
    /// vendor commands (e.g. Megatec <c>Q1\r</c>) or push configuration
    /// state to a device.
    /// </summary>
    /// <param name="report">The report to send — ID byte plus payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="HidTransferException">
    /// Thrown if the device is disconnected, locked by a vendor driver,
    /// or rejected the payload.
    /// </exception>
    public Task WriteFeatureReportAsync(HidReport report, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.WriteFeatureReportAsync(report, ct);
    }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens a platform handle to the HID device described by <paramref name="deviceInfo"/>
    /// and returns a <see cref="HidDevice"/> ready for I/O.
    /// </summary>
    /// <param name="deviceInfo">
    /// The enumeration snapshot identifying the device to open. Must not be null and
    /// must have a non-null, non-empty <see cref="DeviceInfo.Id"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open <see cref="HidDevice"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="deviceInfo"/> is null.
    /// </exception>
    /// <exception cref="HidAccessDeniedException">
    /// Thrown when the OS denies access to the device — exclusive driver lock
    /// (keyboards, mice on Windows) or insufficient privileges.
    /// </exception>
    /// <exception cref="HidDeviceNotFoundException">
    /// Thrown when the device is no longer present — unplugged between enumeration
    /// and open, or the device node does not exist.
    /// </exception>
    /// <exception cref="HidException">
    /// Thrown for any other HID-level failure (caps read error, unknown Win32 error, etc.).
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown on platforms where HID I/O is not yet implemented.
    /// </exception>
    public static Task<HidDevice> OpenAsync(
        DeviceInfo deviceInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInfo.Id);

        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
            return OpenWindowsAsync(deviceInfo, ct);

        if (OperatingSystem.IsLinux())
            return OpenLinuxAsync(deviceInfo, ct);

        throw new PlatformNotSupportedException(
            $"HidDevice.OpenAsync is not yet implemented on {Environment.OSVersion.Platform}. " +
            "The macOS (IOHIDDeviceOpen) backend is planned.");
    }

    [SupportedOSPlatform("windows")]
    private static Task<HidDevice> OpenWindowsAsync(DeviceInfo deviceInfo, CancellationToken ct)
    {
        // Opening a HID handle is synchronous at the OS level (CreateFile);
        // wrap in Task.Run so we don't block the caller's thread.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var backend = Windows.WindowsHidBackend.Open(deviceInfo.Id);
            return new HidDevice(deviceInfo, backend);
        }, ct);
    }

    [SupportedOSPlatform("linux")]
    private static Task<HidDevice> OpenLinuxAsync(DeviceInfo deviceInfo, CancellationToken ct)
    {
        // open(2) plus the descriptor ioctls are synchronous; wrap in
        // Task.Run so we don't block the caller's thread.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var backend = Linux.LinuxHidBackend.Open(deviceInfo.Id);
            return new HidDevice(deviceInfo, backend);
        }, ct);
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _backend.DisposeAsync().ConfigureAwait(false);
    }
}
