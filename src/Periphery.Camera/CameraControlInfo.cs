// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>Metadata for a single camera control (exposure, focus, etc.).</summary>
public sealed record CameraControlInfo(
    CameraControlKind Kind,
    string Name,
    double? MinValue,
    double? MaxValue,
    double? Step,
    double? DefaultValue,
    bool SupportsAutoMode,
    bool IsReadOnly);
