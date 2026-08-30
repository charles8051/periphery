using System.Collections.Immutable;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Pins the APPLY-side frame convention (issue #143, follow-up to #137): a
/// desired <see cref="MonitorConfiguration.Mode"/> is in the panel's
/// <b>native</b> frame — the same frame the read model reports as
/// <see cref="MonitorLayoutEntry.CurrentMode"/> — so <c>LayoutDiff.IsSatisfiedBy</c>
/// compares like-for-like. Before this fix the contract documented <c>Mode</c>
/// in the transposed final-orientation frame, so a portrait panel could never
/// satisfy and the converger thrashed. These are pure — no CCD, no hardware.
/// </summary>
public class MonitorApplyFrameTests
{
    private static MonitorLayoutEntry Entry(
        string id, bool isPrimary, DisplayMode nativeMode,
        MonitorOrientation orientation, DisplayPosition position) =>
        new(id, id, isPrimary, nativeMode, PreferredMode: null, orientation,
            MonitorOutputTechnology.Other, position,
            SupportedModes: ImmutableArray<DisplayMode>.Empty);

    private static MonitorLayout Layout(params MonitorLayoutEntry[] entries) =>
        new(entries.ToImmutableArray(), MonitorLayoutAvailability.Available);

    // A portrait-mounted 1920x1080 panel: native mode is landscape 1920x1080,
    // rotated to Portrait — exactly the shape #137 verified on real hardware.
    private static MonitorLayout PortraitPanel() =>
        Layout(Entry("PNP\\A", isPrimary: true,
            new DisplayMode(1920, 1080, 60), MonitorOrientation.Portrait, new DisplayPosition(0, 0)));

    [Fact]
    public void IsSatisfiedBy_PortraitPanel_NativeModePlusPortrait_IsSatisfied()
    {
        // Desired declares the NATIVE mode (1920x1080) + Portrait — matches the
        // live entry, so no apply is needed. This is the case that could never
        // be satisfied under the old transposed-frame contract.
        var desired = new[]
        {
            new MonitorConfiguration("PNP\\A",
                Mode: new DisplayMode(1920, 1080, 60),
                Orientation: MonitorOrientation.Portrait),
        };

        Assert.True(LayoutDiff.IsSatisfiedBy(PortraitPanel(), desired));
    }

    [Fact]
    public void IsSatisfiedBy_PortraitPanel_TransposedMode_IsNotSatisfied()
    {
        // The OLD contract's value (1080x1920, desktop/final frame) must NOT
        // match the native CurrentMode — this guards against regressing the
        // apply frame back to the transposed one.
        var desired = new[]
        {
            new MonitorConfiguration("PNP\\A",
                Mode: new DisplayMode(1080, 1920, 60),
                Orientation: MonitorOrientation.Portrait),
        };

        Assert.False(LayoutDiff.IsSatisfiedBy(PortraitPanel(), desired));
    }

    [Fact]
    public void IsSatisfiedBy_LandscapePanel_NativeModeUnchanged_IsSatisfied()
    {
        var landscape = Layout(Entry("PNP\\A", isPrimary: true,
            new DisplayMode(1920, 1080, 60), MonitorOrientation.Landscape, new DisplayPosition(0, 0)));
        var desired = new[]
        {
            new MonitorConfiguration("PNP\\A",
                Mode: new DisplayMode(1920, 1080, 60),
                Orientation: MonitorOrientation.Landscape),
        };

        Assert.True(LayoutDiff.IsSatisfiedBy(landscape, desired));
    }

    [Fact]
    public void IsSatisfiedBy_RotationIntentDiffersFromLive_IsNotSatisfied()
    {
        // Live landscape, desired portrait (same native mode) — the orientation
        // axis alone makes it unsatisfied, so the applier will set the rotation.
        var landscape = Layout(Entry("PNP\\A", isPrimary: true,
            new DisplayMode(1920, 1080, 60), MonitorOrientation.Landscape, new DisplayPosition(0, 0)));
        var desired = new[]
        {
            new MonitorConfiguration("PNP\\A",
                Mode: new DisplayMode(1920, 1080, 60),
                Orientation: MonitorOrientation.Portrait),
        };

        Assert.False(LayoutDiff.IsSatisfiedBy(landscape, desired));
    }

    [Fact]
    public void ResolvePositions_PrimaryAtNonOrigin_TranslatesEveryMonitorToPutPrimaryAtOrigin()
    {
        var current = Layout(
            Entry("PNP\\A", isPrimary: false, new DisplayMode(1920, 1080, 60),
                MonitorOrientation.Landscape, new DisplayPosition(1920, 0)),
            Entry("PNP\\B", isPrimary: true, new DisplayMode(1920, 1080, 60),
                MonitorOrientation.Landscape, new DisplayPosition(0, 0)));

        // Make A the primary while it sits at (1920,0): the whole desktop
        // translates so A lands at the origin and B shifts to (-1920,0).
        var desired = new[] { new MonitorConfiguration("PNP\\A", IsPrimary: true) };

        var positions = LayoutDiff.ResolvePositions(current, desired);

        Assert.Equal(new DisplayPosition(0, 0), positions["PNP\\A"]);
        Assert.Equal(new DisplayPosition(-1920, 0), positions["PNP\\B"]);
    }

    [Fact]
    public void ResolvePositions_ExplicitPositionWins_OverObserved()
    {
        var current = Layout(
            Entry("PNP\\A", isPrimary: true, new DisplayMode(1920, 1080, 60),
                MonitorOrientation.Landscape, new DisplayPosition(0, 0)),
            Entry("PNP\\B", isPrimary: false, new DisplayMode(768, 1024, 60),
                MonitorOrientation.Portrait, new DisplayPosition(1920, 0)));

        var desired = new[] { new MonitorConfiguration("PNP\\B", Position: new DisplayPosition(-768, -56)) };

        var positions = LayoutDiff.ResolvePositions(current, desired);

        Assert.Equal(new DisplayPosition(-768, -56), positions["PNP\\B"]);
        Assert.Equal(new DisplayPosition(0, 0), positions["PNP\\A"]); // untouched
    }
}
