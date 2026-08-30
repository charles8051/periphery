// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Camera.Internal;

namespace Periphery.Camera;

/// <summary>
/// Layer 1 camera I/O primitive. Represents an open platform handle to a camera
/// device and exposes capability discovery, controls, and session creation.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an instance via <see cref="OpenAsync(DeviceInfo, CancellationToken, ILogger{CameraDevice}?)"/>,
/// which crosses the discovery/I/O boundary by activating the OS camera stack.
/// </para>
/// <para>
/// For application-level capture use <see cref="CameraSession"/> (via
/// <see cref="OpenSessionAsync"/> or <see cref="CameraSession.OpenAsync"/>).
/// For reconnect-resilient lifecycle use
/// <c>DeviceSessionHost&lt;CameraSession&gt;</c>.
/// </para>
/// </remarks>
public sealed class CameraDevice : IAsyncDisposable
{
    internal readonly ICameraBackend _backend;
    private readonly ILogger<CameraDevice> _logger;
    private bool _hasActiveSession;
    private bool _disposed;

    internal CameraDevice(
        DeviceInfo deviceInfo, ICameraBackend backend, ILogger<CameraDevice>? logger = null)
    {
        DeviceInfo = deviceInfo;
        _backend = backend;
        _logger = logger ?? NullLogger<CameraDevice>.Instance;
    }

    // ── Discovery context ──────────────────────────────────────────────

    /// <summary>The enumeration snapshot from which this device was opened.</summary>
    public DeviceInfo DeviceInfo { get; }

    /// <summary>Backend-native endpoint identifier for diagnostics.</summary>
    public string NativeEndpointId => _backend.NativeEndpointId;

    // ── Discovery helper ───────────────────────────────────────────────

    /// <summary>
    /// One-shot snapshot of the cameras currently visible to the OS.
    /// Sugar for <c>Devices.Enumerate().OfCategory(DeviceCategory.Camera).ToListAsync(ct)</c>;
    /// returns an immediate <see cref="IReadOnlyList{T}"/> rather than an
    /// <see cref="IAsyncEnumerable{T}"/> so UI consumers can bind directly.
    /// </summary>
    /// <param name="ct">Cancellation token for the enumeration.</param>
    /// <returns>The currently-detected cameras. Empty if none are connected.</returns>
    public static async Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(
        CancellationToken ct = default)
    {
        var list = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Camera)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return list;
    }

    // ── Snapshot helper (ADR-0026) ─────────────────────────────────────

    /// <summary>
    /// Opens the camera stack briefly to read handle-gated metadata (formats,
    /// controls) without keeping the device open. This is the ADR-0026 static
    /// snapshot helper.
    /// </summary>
    public static async Task<CameraSnapshot> ReadSnapshotAsync(
        DeviceInfo device,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var backend = CreateBackend(device);
        try
        {
            await backend.OpenAsync(ct).ConfigureAwait(false);
            var formats = await backend.GetFormatsAsync(ct).ConfigureAwait(false);
            var controls = await backend.GetControlsAsync(ct).ConfigureAwait(false);
            return new CameraSnapshot(backend.NativeEndpointId, formats, controls);
        }
        finally
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Instance-level snapshot from the already-open device.</summary>
    public async Task<CameraSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var formats = await _backend.GetFormatsAsync(ct).ConfigureAwait(false);
        var controls = await _backend.GetControlsAsync(ct).ConfigureAwait(false);
        return new CameraSnapshot(NativeEndpointId, formats, controls);
    }

    // ── Format enumeration ─────────────────────────────────────────────

    public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.GetFormatsAsync(ct);
    }

    // ── Controls ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.GetControlsAsync(ct);
    }

    /// <summary>
    /// Read one control's current value and mode.
    /// </summary>
    /// <returns>
    /// The reading, or <c>null</c> when this device does not expose
    /// <paramref name="control"/> at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="SetControlAsync"/> and
    /// <see cref="ResetControlAsync"/>, and the piece that makes them reversible:
    /// without it a caller can put a control somewhere but cannot record where it
    /// was, so "restore what I found" is not expressible and the best available
    /// is "return it to automatic".
    /// </para>
    /// <para>
    /// A reading, not a description — see <see cref="CameraControlState"/> for why
    /// it is separate from <see cref="GetControlsAsync"/>, and
    /// <see cref="CameraControlMode.Unknown"/> for why the mode may be
    /// indeterminate on a device that reports a value quite happily.
    /// </para>
    /// </remarks>
    public Task<CameraControlState?> GetControlAsync(
        CameraControlKind control, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.GetControlAsync(control, ct);
    }

    public Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.SetControlAsync(control, value, ct);
    }

    public Task ResetControlAsync(CameraControlKind control, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.ResetControlAsync(control, ct);
    }

    // ── Session creation ───────────────────────────────────────────────

    /// <summary>
    /// Creates a configured capture session on this device. The caller owns both
    /// the device and the session independently — disposing the session does not
    /// dispose the device.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if another session is already active on this device.
    /// </exception>
    public async Task<CameraSession> OpenSessionAsync(
        CameraConfiguration configuration,
        CameraSessionOptions? options = null,
        CancellationToken ct = default,
        ILogger<CameraSession>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);

        if (_hasActiveSession)
            throw new InvalidOperationException("A session is already active on this device. Dispose the existing session before opening a new one.");

        await _backend.ConfigureAsync(configuration, ct).ConfigureAwait(false);
        _hasActiveSession = true;
        var session = new CameraSession(this, ownsDevice: false, _backend, configuration, options ?? new(), logger, timeProvider);
        session.LogSessionOpened();
        return session;
    }

    internal void OnSessionDisposed() => _hasActiveSession = false;

    // ── Factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a platform handle to the camera device described by
    /// <paramref name="device"/> and returns a <see cref="CameraDevice"/>
    /// ready for capability queries and session creation.
    /// </summary>
    public static async Task<CameraDevice> OpenAsync(
        DeviceInfo device,
        CancellationToken ct = default,
        ILogger<CameraDevice>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Id);
        ct.ThrowIfCancellationRequested();

        var backend = CreateBackend(device);
        try
        {
            await backend.OpenAsync(ct).ConfigureAwait(false);
            var camera = new CameraDevice(device, backend, logger);
            camera._logger.LogInformation(
                "Camera device opened: {DeviceName} ({NativeEndpoint})",
                device.Name ?? "(unnamed)", backend.NativeEndpointId);
            return camera;
        }
        catch
        {
            await backend.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ── Backend selection ──────────────────────────────────────────────

    internal static Func<DeviceInfo, ICameraBackend>? BackendFactory { get; set; }

    private static ICameraBackend CreateBackend(DeviceInfo device)
    {
        if (BackendFactory is { } factory)
            return factory(device);

        if (OperatingSystem.IsWindows())
            return CreateWindowsBackend(device);

        if (OperatingSystem.IsLinux())
            return CreateLinuxBackend(device);

        throw new PlatformNotSupportedException(
            $"CameraDevice is not yet implemented on {Environment.OSVersion.Platform}. " +
            "The AVFoundation (macOS) backend is planned.");
    }

    [SupportedOSPlatform("windows")]
    private static ICameraBackend CreateWindowsBackend(DeviceInfo device)
    {
        return new Windows.MfCameraBackend(device);
    }

    [SupportedOSPlatform("linux")]
    private static ICameraBackend CreateLinuxBackend(DeviceInfo device)
    {
        return new Linux.V4l2CameraBackend(device);
    }

    // ── Disposal ───────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _backend.DisposeAsync().ConfigureAwait(false);
    }
}
