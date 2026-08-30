// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Describes a single format that a camera device supports: resolution,
/// pixel format, frame-rate range, and transport mode.
/// </summary>
public sealed record CameraFormat(
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    Rational MinFrameRate,
    Rational MaxFrameRate,
    CameraTransport Transport);
