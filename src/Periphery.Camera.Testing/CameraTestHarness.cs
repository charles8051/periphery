// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Camera.Internal;

namespace Periphery.Camera.Testing;

/// <summary>
/// Constructs a <see cref="CameraDevice"/> / <see cref="CameraSession"/> directly
/// over an <see cref="InMemoryCameraBackend"/>, with <b>no</b> global-state
/// redirect. Use this when the code under test accepts an already-open session or
/// device; use <see cref="CameraTestScope"/> instead when it opens from a
/// <see cref="DeviceInfo"/> on its own.
/// </summary>
public static class CameraTestHarness
{
    /// <summary>
    /// Open a <see cref="CameraDevice"/> over <paramref name="backend"/>. The
    /// caller owns disposal of the returned device (which disposes the backend).
    /// </summary>
    public static async Task<CameraDevice> OpenDeviceAsync(
        InMemoryCameraBackend backend,
        DeviceInfo? device = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        device ??= CameraTestFormats.CreateDeviceInfo();
        await ((ICameraBackend)backend).OpenAsync(ct).ConfigureAwait(false);
        return new CameraDevice(device, backend);
    }

    /// <summary>
    /// Open a capture-ready <see cref="CameraSession"/> over
    /// <paramref name="backend"/>. Disposing the session disposes the device and
    /// backend (<c>ownsDevice: true</c>), matching
    /// <c>CameraSession.OpenAsync</c>.
    /// </summary>
    /// <param name="backend">The fake backend to capture from.</param>
    /// <param name="configuration">Format/target to apply. Defaults to
    /// <see cref="CameraTestFormats.Vga"/>.</param>
    /// <param name="device">Device identity. Defaults to
    /// <see cref="CameraTestFormats.CreateDeviceInfo(string, string)"/>.</param>
    /// <param name="options">Session options (buffer count, exhaustion policy).</param>
    /// <param name="timeProvider">Clock for the session's frame-timeout and
    /// bounded-stop delays — pass a <c>FakeTimeProvider</c> to drive timeouts
    /// deterministically.</param>
    /// <param name="ct">Cancellation for the open.</param>
    public static async Task<CameraSession> OpenSessionAsync(
        InMemoryCameraBackend backend,
        CameraConfiguration? configuration = null,
        DeviceInfo? device = null,
        CameraSessionOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        device ??= CameraTestFormats.CreateDeviceInfo();
        configuration ??= new CameraConfiguration(CameraTestFormats.Vga);
        options ??= new CameraSessionOptions();

        var io = (ICameraBackend)backend;
        await io.OpenAsync(ct).ConfigureAwait(false);
        await io.ConfigureAsync(configuration, ct).ConfigureAwait(false);

        var cameraDevice = new CameraDevice(device, backend);
        return new CameraSession(
            cameraDevice, ownsDevice: true, backend, configuration, options, logger: null, timeProvider);
    }
}
