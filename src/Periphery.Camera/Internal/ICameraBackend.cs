// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Internal;

/// <summary>
/// Platform abstraction for camera device I/O. Each platform (Windows Media
/// Foundation, Linux V4L2, macOS AVFoundation) provides an implementation.
/// </summary>
internal interface ICameraBackend : IAsyncDisposable
{
    string NativeEndpointId { get; }

    Task OpenAsync(CancellationToken ct);
    Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct);
    Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct);

    /// <summary>
    /// Read one control's current value and mode, or null when the device does
    /// not expose that control.
    /// </summary>
    Task<CameraControlState?> GetControlAsync(CameraControlKind control, CancellationToken ct);
    Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct);
    Task ResetControlAsync(CameraControlKind control, CancellationToken ct);

    Task ConfigureAsync(CameraConfiguration configuration, CancellationToken ct);
    Task StartCaptureAsync(CancellationToken ct);
    Task<RawCameraFrame> ReadRawFrameAsync(CancellationToken ct);
    Task StopCaptureAsync();
}
