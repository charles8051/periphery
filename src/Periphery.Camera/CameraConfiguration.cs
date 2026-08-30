// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// A negotiated camera capture configuration: selected format and target frame
/// rate. Late-frame drop/block behavior is governed by
/// <see cref="CameraSessionOptions.ExhaustionPolicy"/>, not here.
/// </summary>
public sealed record CameraConfiguration(
    CameraFormat Format,
    Rational? TargetFrameRate = null);
