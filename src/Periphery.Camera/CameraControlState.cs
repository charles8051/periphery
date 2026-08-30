// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// What a camera control is set to <i>right now</i>, and whether the device is
/// driving it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately separate from <see cref="CameraControlInfo"/>.</b> That type
/// describes a control's fixed shape — the range, the step, the default, whether
/// it can be written at all — and is stable for as long as the device is the
/// device. This one is a <i>reading</i>: true at the moment it was taken and
/// potentially false immediately after, because on most cameras the point of
/// these controls is that the device keeps moving them.
/// </para>
/// <para>
/// Folding the reading into the descriptor would have made
/// <c>GetControlsAsync</c> — a capability query — do a round of per-control IO
/// and hand back values that go stale in the caller's hand. Keeping them apart
/// lets a consumer ask each question at the cost it deserves.
/// </para>
/// </remarks>
/// <param name="Kind">Which control this reading is of.</param>
/// <param name="Value">
/// The value in the control's own units, on the scale
/// <see cref="CameraControlInfo.MinValue"/> and <see cref="CameraControlInfo.MaxValue"/>
/// describe.
/// </param>
/// <param name="Mode">
/// Whether the device is driving the control. May be
/// <see cref="CameraControlMode.Unknown"/> — see that member for why it is not
/// safe to read as "manual".
/// </param>
public sealed record CameraControlState(
    CameraControlKind Kind,
    double Value,
    CameraControlMode Mode);
