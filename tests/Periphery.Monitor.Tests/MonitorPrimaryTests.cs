using System.Collections.Generic;
using Periphery.Monitor.Windows;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Covers <see cref="MonitorPrimary.SelectPrimaryIndex"/> — the pure core that
/// replaced the position==(0,0) primary inference (issue #138). The CCD interop
/// shell isn't unit-testable without hardware, so the primary decision lives
/// here as a total function over parsed path facts.
/// </summary>
public class MonitorPrimaryTests
{
    private static MonitorPrimary.PathFacts Path(string? gdi, int x, int y) =>
        new(gdi, new DisplayPosition(x, y));

    // (a) Normal dual-monitor extended desktop: DISPLAY1 at the origin is the
    // GDI primary; exactly one primary, and it's that one.
    [Fact]
    public void DualExtended_PicksTheGdiPrimary()
    {
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY1", 0, 0),
            Path(@"\\.\DISPLAY2", 1920, 0),
        };

        Assert.Equal(0, MonitorPrimary.SelectPrimaryIndex(paths, @"\\.\DISPLAY1"));
    }

    // (b) Clone / duplicate mode: two active paths share one source at the
    // origin. The old position==(0,0) rule flagged BOTH; selection returns a
    // single index, so exactly one primary survives.
    [Fact]
    public void CloneMode_YieldsExactlyOnePrimary_NotTwo()
    {
        // Both targets mirror one desktop → same source name, same origin.
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY1", 0, 0),
            Path(@"\\.\DISPLAY1", 0, 0),
        };

        int idx = MonitorPrimary.SelectPrimaryIndex(paths, @"\\.\DISPLAY1");

        Assert.Equal(0, idx);
        Assert.Equal(1, CountPrimaries(paths, idx));
    }

    // (b′) Clone mode with the authoritative signal unavailable (GDI query
    // failed): the (0,0) fallback still collapses duplicates to one primary.
    [Fact]
    public void CloneMode_FallbackToOrigin_StillOnePrimary()
    {
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY1", 0, 0),
            Path(@"\\.\DISPLAY1", 0, 0),
        };

        int idx = MonitorPrimary.SelectPrimaryIndex(paths, gdiPrimarySourceName: null);

        Assert.Equal(0, idx);
        Assert.Equal(1, CountPrimaries(paths, idx));
    }

    // (c) A non-origin primary is still detected: the real primary sits to the
    // right at (1920,0) while another monitor holds the origin. Selection must
    // follow the GDI signal, not position.
    [Fact]
    public void NonOriginPrimary_IsDetectedByGdiSignal_NotPosition()
    {
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY1", -1920, 0), // left monitor, at origin's left
            Path(@"\\.\DISPLAY2", 0, 0),     // sits at the desktop origin
            Path(@"\\.\DISPLAY3", 1920, 0),  // the actual primary
        };

        Assert.Equal(2, MonitorPrimary.SelectPrimaryIndex(paths, @"\\.\DISPLAY3"));
    }

    // The virtual-display-at-origin case (issue #138): an IddCx display parked
    // at (0,0) is NOT the GDI primary and must not steal the flag from the real
    // panel, even though it holds the origin.
    [Fact]
    public void VirtualDisplayAtOrigin_DoesNotStealPrimary()
    {
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY3", 0, 0),     // indirect virtual display at origin
            Path(@"\\.\DISPLAY1", 1920, 0),  // real primary, off origin
        };

        Assert.Equal(1, MonitorPrimary.SelectPrimaryIndex(paths, @"\\.\DISPLAY1"));
    }

    // GDI name matching is case-insensitive (the two APIs can differ in case).
    [Fact]
    public void GdiPrimaryMatch_IsCaseInsensitive()
    {
        var paths = new List<MonitorPrimary.PathFacts> { Path(@"\\.\Display1", 500, 500) };

        Assert.Equal(0, MonitorPrimary.SelectPrimaryIndex(paths, @"\\.\DISPLAY1"));
    }

    // When the GDI primary matches no active path, fall back to the origin.
    [Fact]
    public void UnmatchedGdiName_FallsBackToOrigin()
    {
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY1", 1920, 0),
            Path(@"\\.\DISPLAY2", 0, 0),
        };

        Assert.Equal(1, MonitorPrimary.SelectPrimaryIndex(paths, @"\\.\DISPLAY9"));
    }

    // No signal and no monitor at the origin → no primary (-1), rather than a
    // wrong guess.
    [Fact]
    public void NoOriginAndNoSignal_ReturnsNoPrimary()
    {
        var paths = new List<MonitorPrimary.PathFacts>
        {
            Path(@"\\.\DISPLAY1", 100, 100),
            Path(@"\\.\DISPLAY2", 1920, 0),
        };

        Assert.Equal(-1, MonitorPrimary.SelectPrimaryIndex(paths, gdiPrimarySourceName: null));
    }

    [Fact]
    public void EmptyTopology_ReturnsNoPrimary()
    {
        Assert.Equal(-1, MonitorPrimary.SelectPrimaryIndex([], @"\\.\DISPLAY1"));
    }

    private static int CountPrimaries(IReadOnlyList<MonitorPrimary.PathFacts> paths, int primaryIdx)
    {
        // Model how the shell stamps IsPrimary: one index, so never more than one.
        int count = 0;
        for (int i = 0; i < paths.Count; i++)
            if (i == primaryIdx)
                count++;
        return count;
    }
}
