// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Handle-gated pre-session device snapshot. Contains information that requires
/// activating the camera stack (format list, control metadata) but does not keep
/// the device open. See ADR-0026: enrichers must not open handles — this helper
/// is the explicit alternative.
/// </summary>
public sealed record CameraSnapshot(
    string NativeEndpointId,
    IReadOnlyList<CameraFormat> Formats,
    IReadOnlyList<CameraControlInfo> Controls);
