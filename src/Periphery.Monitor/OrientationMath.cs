// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// Pure orientation arithmetic shared by the display-mode backends, so the
/// classic DEVMODE rotation bug (forgetting to swap width and height when
/// crossing the landscape/portrait boundary) lives behind one tested helper.
/// </summary>
internal static class OrientationMath
{
    /// <summary>True when the orientation is portrait-class (90° or 270°).</summary>
    internal static bool IsPortrait(MonitorOrientation orientation) =>
        orientation is MonitorOrientation.Portrait or MonitorOrientation.PortraitFlipped;

    /// <summary>
    /// True when moving between the two orientations crosses the
    /// landscape/portrait boundary, i.e. the mode's width and height must swap.
    /// </summary>
    internal static bool SwapsDimensions(MonitorOrientation from, MonitorOrientation to) =>
        IsPortrait(from) != IsPortrait(to);

    /// <summary>
    /// The (width, height) a mode takes after moving from
    /// <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    internal static (int Width, int Height) Reframe(
        int width, int height, MonitorOrientation from, MonitorOrientation to) =>
        SwapsDimensions(from, to) ? (height, width) : (width, height);
}
