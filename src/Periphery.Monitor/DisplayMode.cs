// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// One display mode: pixel dimensions plus refresh rate in integer hertz.
/// </summary>
/// <remarks>
/// <b>Frame is a property of the field, not of this type.</b> A
/// <c>DisplayMode</c> is a bare (width, height, refresh) triple; whether its
/// width/height are the panel's <i>native/unrotated</i> pixels or the
/// <i>current-orientation/desktop</i> pixels depends on which field carries it.
/// The panel plane speaks native pixels
/// (<see cref="MonitorLayoutEntry.CurrentMode"/>,
/// <see cref="MonitorLayoutEntry.PreferredMode"/>, and the entry's supported
/// modes are all native — a portrait-rotated 1280x720 panel reports its mode as
/// 1280x720); the desktop-layout plane speaks rotated pixels
/// (<see cref="MonitorLayoutEntry.DesktopSize"/> reports that same panel as
/// 720x1280). Never assume a frame — read the field's own documentation.
/// Refresh is integer Hz because that is what the mode-set APIs speak
/// (<c>DEVMODE.dmDisplayFrequency</c>); fractional broadcast rates (29.97)
/// round to the OS-reported integer. (ADR-0058's sketch said <c>Rational</c>,
/// but that type lives in <c>Periphery.Camera</c> — a spoke — and the star
/// topology forbids spoke-to-spoke references; integer Hz is also the honest
/// unit for the underlying API.)
/// </remarks>
public sealed record DisplayMode(int Width, int Height, int RefreshRateHz)
{
    public override string ToString() => $"{Width}x{Height}@{RefreshRateHz}";
}
