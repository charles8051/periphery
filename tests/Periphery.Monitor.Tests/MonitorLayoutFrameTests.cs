using System.Collections.Immutable;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Pins the read-model frame convention (issue #137): a monitor entry reports
/// its panel mode in the <b>native/unrotated</b> frame (<c>CurrentMode</c>) and
/// its virtual-desktop footprint in the <b>rotated</b> frame
/// (<c>DesktopSize</c>). These are the guard that stops the frame drifting back
/// into the ambiguity behind the "-312 vs -56" position bug and the transposed
/// remote-view crop.
/// </summary>
public class MonitorLayoutFrameTests
{
    private static MonitorLayoutEntry Entry(
        DisplayMode nativeMode, MonitorOrientation orientation) =>
        new(
            "PNP\\FRAME-TEST",
            "Frame Test Panel",
            IsPrimary: true,
            CurrentMode: nativeMode,
            PreferredMode: nativeMode,
            orientation,
            MonitorOutputTechnology.Other,
            new DisplayPosition(0, 0),
            ImmutableArray<DisplayMode>.Empty);

    [Fact]
    public void PortraitPanel_ReportsNativeMode_AndTransposedDesktopFootprint()
    {
        // A panel whose native mode is 1280x720, rotated to portrait.
        var entry = Entry(new DisplayMode(1280, 720, 60), MonitorOrientation.Portrait);

        // CurrentMode stays in the documented native/source frame...
        Assert.Equal(new DisplayMode(1280, 720, 60), entry.CurrentMode);
        // ...while the desktop footprint is the rotated 720x1280.
        Assert.Equal(new DisplaySize(720, 1280), entry.DesktopSize);
    }

    [Fact]
    public void PortraitFlipped_AlsoTransposesTheFootprint()
    {
        var entry = Entry(new DisplayMode(1280, 720, 60), MonitorOrientation.PortraitFlipped);

        Assert.Equal(new DisplayMode(1280, 720, 60), entry.CurrentMode);
        Assert.Equal(new DisplaySize(720, 1280), entry.DesktopSize);
    }

    [Theory]
    [InlineData(MonitorOrientation.Landscape)]
    [InlineData(MonitorOrientation.LandscapeFlipped)]
    public void LandscapePanel_DesktopFootprintMatchesTheNativeMode(MonitorOrientation orientation)
    {
        var entry = Entry(new DisplayMode(1920, 1080, 60), orientation);

        Assert.Equal(new DisplaySize(1920, 1080), entry.DesktopSize);
        // Native mode and footprint coincide when no landscape/portrait crossing.
        Assert.Equal(entry.CurrentMode.Width, entry.DesktopSize.Width);
        Assert.Equal(entry.CurrentMode.Height, entry.DesktopSize.Height);
    }

    [Fact]
    public void DesktopSize_IsDerived_NotStored_SoItCannotDriftFromOrientation()
    {
        var native = new DisplayMode(2560, 1440, 144);
        var landscape = Entry(native, MonitorOrientation.Landscape);
        var portrait = Entry(native, MonitorOrientation.Portrait);

        // Same native CurrentMode, orientation alone flips the footprint.
        Assert.Equal(new DisplaySize(2560, 1440), landscape.DesktopSize);
        Assert.Equal(new DisplaySize(1440, 2560), portrait.DesktopSize);
    }
}
