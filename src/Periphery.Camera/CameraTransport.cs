// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Indicates whether the camera delivers frames as raw uncompressed pixel data
/// or as a compressed stream (e.g. MJPEG) that requires decode before use.
/// </summary>
public enum CameraTransport
{
    /// <summary>Frame data is uncompressed pixels in the declared pixel format.</summary>
    Uncompressed = 0,

    /// <summary>Frame data is a compressed payload (e.g. MJPEG) requiring decode.</summary>
    Compressed,
}
