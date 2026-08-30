// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// A width×height footprint in pixels — no refresh, no position. Used for the
/// space a monitor occupies on the virtual desktop
/// (<see cref="MonitorLayoutEntry.DesktopSize"/>), which is a size fact, not a
/// settable mode. Distinct from <see cref="DisplayMode"/> (which carries a
/// refresh rate and names a settable panel mode).
/// </summary>
public sealed record DisplaySize(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}
