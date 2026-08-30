using System.Collections.Immutable;

namespace Periphery.Monitor.Tests;

public class LayoutDiffTests
{
    private static MonitorLayoutEntry Entry(
        string id, bool primary = false, int w = 1920, int h = 1080, int hz = 60,
        MonitorOrientation orientation = MonitorOrientation.Landscape,
        int x = 0, int y = 0) =>
        new(id, $"Fake {id}", primary, new DisplayMode(w, h, hz), null,
            orientation, MonitorOutputTechnology.Other, new DisplayPosition(x, y),
            ImmutableArray<DisplayMode>.Empty);

    private static MonitorLayout TwoMonitorLayout() => new(
    [
        Entry("A", primary: true),
        Entry("B", x: 1920, y: 0),
    ],
    MonitorLayoutAvailability.Available);

    [Fact]
    public void Satisfied_WhenEveryRequestedAxisMatches()
    {
        var layout = TwoMonitorLayout();

        Assert.True(LayoutDiff.IsSatisfiedBy(layout,
        [
            new MonitorConfiguration("A", Mode: new DisplayMode(1920, 1080, 60), IsPrimary: true),
            new MonitorConfiguration("B", Position: new DisplayPosition(1920, 0)),
        ]));
    }

    [Fact]
    public void NotSatisfied_OnAnyDriftedAxis()
    {
        var layout = TwoMonitorLayout();

        Assert.False(LayoutDiff.IsSatisfiedBy(layout,
            [new MonitorConfiguration("A", Mode: new DisplayMode(1280, 720, 60))]));
        Assert.False(LayoutDiff.IsSatisfiedBy(layout,
            [new MonitorConfiguration("A", Orientation: MonitorOrientation.Portrait)]));
        Assert.False(LayoutDiff.IsSatisfiedBy(layout,
            [new MonitorConfiguration("B", IsPrimary: true)]));
        Assert.False(LayoutDiff.IsSatisfiedBy(layout,
            [new MonitorConfiguration("B", Position: new DisplayPosition(0, 1080))]));
    }

    [Fact]
    public void NullAxes_AreIgnored_AndUnknownIdsAreNotSatisfied()
    {
        var layout = TwoMonitorLayout();

        // Only IsPrimary requested — mode/orientation/position drift is irrelevant.
        Assert.True(LayoutDiff.IsSatisfiedBy(layout,
            [new MonitorConfiguration("A", IsPrimary: true)]));

        Assert.False(LayoutDiff.IsSatisfiedBy(layout,
            [new MonitorConfiguration("GHOST", IsPrimary: true)]));
    }

    [Fact]
    public void ResolvePositions_PrimaryDesignation_TranslatesWholeTopology()
    {
        var layout = TwoMonitorLayout();

        var positions = LayoutDiff.ResolvePositions(layout,
            [new MonitorConfiguration("B", IsPrimary: true)]);

        // B moves to origin; A shifts left by B's old offset.
        Assert.Equal(new DisplayPosition(0, 0), positions["B"]);
        Assert.Equal(new DisplayPosition(-1920, 0), positions["A"]);
    }

    [Fact]
    public void ResolvePositions_ExplicitPositionWins_ThenPrimaryTranslates()
    {
        var layout = TwoMonitorLayout();

        var positions = LayoutDiff.ResolvePositions(layout,
        [
            new MonitorConfiguration("B", Position: new DisplayPosition(0, 1080), IsPrimary: true),
        ]);

        // B pinned below A, then designated primary: translate by (0,-1080).
        Assert.Equal(new DisplayPosition(0, 0), positions["B"]);
        Assert.Equal(new DisplayPosition(0, -1080), positions["A"]);
    }

    [Fact]
    public void ResolvePositions_AlreadyPrimary_IsANoOpTranslation()
    {
        var layout = TwoMonitorLayout();

        var positions = LayoutDiff.ResolvePositions(layout,
            [new MonitorConfiguration("A", IsPrimary: true)]);

        Assert.Equal(new DisplayPosition(0, 0), positions["A"]);
        Assert.Equal(new DisplayPosition(1920, 0), positions["B"]);
    }
}
