// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// A monitor's origin in a global virtual-desktop coordinate space.
/// </summary>
/// <remarks>
/// This type models a <b>backend capability</b>, not a universal fact
/// (ADR-0064). It is Windows-CCD-backed today, where every source shares one
/// signed virtual-desktop plane and, by that OS's definition, the primary
/// monitor sits at (0,0) with the others placed relative to it. That framing
/// does not generalize: on Wayland there is no global desktop origin and
/// clients cannot read or set output position, and on X11 position is a RandR
/// CRTC coordinate unrelated to which output is primary. A non-Windows backend
/// that lacks a global origin should not synthesize one; consumers must treat
/// absolute coordinates as meaningful only when the active backend documents a
/// global desktop space.
/// </remarks>
public readonly record struct DisplayPosition(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}
