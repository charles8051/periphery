using System.Drawing;
using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for the pure DisplayConfig geometry helpers (issue #163):
/// the CCD rotation → <see cref="DisplayOrientation"/> translation and the
/// reconciliation of an unrotated source surface with a rotated virtual-desktop
/// origin. Total value transforms — no display hardware, so they run anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class DisplayGeometryTests
{
    // ── FromCcdRotation ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1, DisplayOrientation.Landscape)]        // DISPLAYCONFIG_ROTATION_IDENTITY
    [InlineData(2, DisplayOrientation.Portrait)]         // ..._ROTATE90
    [InlineData(3, DisplayOrientation.LandscapeFlipped)] // ..._ROTATE180
    [InlineData(4, DisplayOrientation.PortraitFlipped)]  // ..._ROTATE270
    public void FromCcdRotation_MapsEveryDefinedValue(int rotation, DisplayOrientation expected)
    {
        Assert.Equal(expected, DisplayGeometry.FromCcdRotation(rotation));
    }

    [Theory]
    [InlineData(0)]           // zero-initialised struct / inactive path
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)] // DISPLAYCONFIG_ROTATION_FORCE_UINT32 territory
    public void FromCcdRotation_IsTotal_UnknownReadsAsLandscape(int rotation)
    {
        Assert.Equal(DisplayOrientation.Landscape, DisplayGeometry.FromCcdRotation(rotation));
    }

    // ── IsPortrait ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DisplayOrientation.Landscape, false)]
    [InlineData(DisplayOrientation.Portrait, true)]
    [InlineData(DisplayOrientation.LandscapeFlipped, false)]
    [InlineData(DisplayOrientation.PortraitFlipped, true)]
    public void IsPortrait_ClassifiesByQuarterTurn(DisplayOrientation orientation, bool expected)
    {
        Assert.Equal(expected, DisplayGeometry.IsPortrait(orientation));
    }

    // ── DesktopBounds ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(DisplayOrientation.Landscape)]
    [InlineData(DisplayOrientation.LandscapeFlipped)]
    public void DesktopBounds_LandscapeClass_KeepsSourceSize(DisplayOrientation orientation)
    {
        var bounds = DisplayGeometry.DesktopBounds(-1920, 0, 1920, 1080, orientation);

        Assert.Equal(new Rectangle(-1920, 0, 1920, 1080), bounds);
    }

    [Theory]
    [InlineData(DisplayOrientation.Portrait)]
    [InlineData(DisplayOrientation.PortraitFlipped)]
    public void DesktopBounds_PortraitClass_TransposesSourceSize(DisplayOrientation orientation)
    {
        var bounds = DisplayGeometry.DesktopBounds(-1080, 0, 1920, 1080, orientation);

        Assert.Equal(new Rectangle(-1080, 0, 1080, 1920), bounds);
    }

    [Fact]
    public void DesktopBounds_RotatedNonPrimary_AbutsNeighbourExactly()
    {
        // The reported repro (issue #163): a 1920×1080 panel left of the primary,
        // rotated to portrait. Windows moves the origin to -1080 because the
        // ROTATED footprint is 1080 wide; the source surface stays 1920×1080.
        // Combining them verbatim produced a 1920-wide rect at x=-1080 that ran to
        // +840 and overlapped the neighbour at x=0 — a layout that cannot exist.
        var rotated = DisplayGeometry.DesktopBounds(-1080, 0, 1920, 1080, DisplayOrientation.Portrait);

        Assert.Equal(new Rectangle(-1080, 0, 1080, 1920), rotated);
        Assert.Equal(0, rotated.Right); // abuts the primary at x=0; no overlap
    }

    [Fact]
    public void DesktopBounds_OriginIsNeverTransposed()
    {
        // Only the size crosses frames. The position already arrives rotated, so a
        // transpose there would be a second, compounding bug.
        var bounds = DisplayGeometry.DesktopBounds(300, -700, 1280, 720, DisplayOrientation.Portrait);

        Assert.Equal(300, bounds.X);
        Assert.Equal(-700, bounds.Y);
    }

    [Fact]
    public void DesktopBounds_TransposeIsInvolutive_OverAFullTurn()
    {
        // Two quarter-turns return the original footprint.
        var quarter = DisplayGeometry.DesktopBounds(0, 0, 1920, 1080, DisplayOrientation.Portrait);
        var half = DisplayGeometry.DesktopBounds(0, 0, 1920, 1080, DisplayOrientation.LandscapeFlipped);

        Assert.Equal(new Size(1080, 1920), quarter.Size);
        Assert.Equal(new Size(1920, 1080), half.Size);
    }
}
