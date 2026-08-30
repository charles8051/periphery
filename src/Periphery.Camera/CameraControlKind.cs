// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Well-known camera control types that can be queried and set via
/// <see cref="CameraDevice.GetControlsAsync"/> and <see cref="CameraDevice.SetControlAsync"/>.
/// </summary>
public enum CameraControlKind
{
    Unknown = 0,
    Brightness,
    Contrast,
    Saturation,
    Sharpness,
    Gain,
    Exposure,
    WhiteBalance,
    Focus,
    Zoom,
    Pan,
    Tilt,
    Torch,
    Gamma,
    Hue,
    BacklightCompensation,
    PowerLineFrequency,
}
