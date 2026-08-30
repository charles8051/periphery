// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Describes the format of a live camera stream. Passed to
/// <see cref="ICameraFrameSink.OnFormatChangedAsync(CameraFormatInfo, System.Threading.CancellationToken)"/>
/// when the upstream camera changes resolution or pixel format mid-stream
/// (e.g. autoswap under bandwidth pressure on some USB chipsets).
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="PixelFormat">Pixel format of the frames being produced.</param>
/// <remarks>
/// Moved into <c>Periphery.Camera</c> from the now-deleted
/// <c>Periphery.Camera.Pipelines</c> per ADR-0045 §3.
/// </remarks>
public sealed record CameraFormatInfo(
    int Width,
    int Height,
    CameraPixelFormat PixelFormat);
