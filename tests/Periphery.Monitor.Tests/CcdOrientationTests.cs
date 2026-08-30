using System.Runtime.Versioning;
using Periphery.Monitor.Windows;

namespace Periphery.Monitor.Tests;

/// <summary>
/// Locks the explicit MonitorOrientation ↔ Windows-encoding mapping
/// (ADR-0064). These are pure value transforms, so they run on any host; the
/// platform attribute only silences the analyzer for the Windows-scoped helper.
/// </summary>
[SupportedOSPlatform("windows")]
public class CcdOrientationTests
{
    [Theory]
    [InlineData(MonitorOrientation.Landscape, 0u)]
    [InlineData(MonitorOrientation.Portrait, 1u)]
    [InlineData(MonitorOrientation.LandscapeFlipped, 2u)]
    [InlineData(MonitorOrientation.PortraitFlipped, 3u)]
    public void DevMode_RoundTrips(MonitorOrientation orientation, uint dmdo)
    {
        Assert.Equal(dmdo, CcdOrientation.ToDevMode(orientation));
        Assert.Equal(orientation, CcdOrientation.FromDevMode(dmdo));
    }

    [Theory]
    [InlineData(MonitorOrientation.Landscape, 1u)]        // DISPLAYCONFIG_ROTATION_IDENTITY
    [InlineData(MonitorOrientation.Portrait, 2u)]         // ROTATE90
    [InlineData(MonitorOrientation.LandscapeFlipped, 3u)] // ROTATE180
    [InlineData(MonitorOrientation.PortraitFlipped, 4u)]  // ROTATE270
    public void CcdRotation_RoundTrips(MonitorOrientation orientation, uint rotation)
    {
        Assert.Equal(rotation, CcdOrientation.ToCcdRotation(orientation));
        Assert.Equal(orientation, CcdOrientation.FromCcdRotation(rotation));
    }

    [Theory]
    [InlineData(0u)]   // below IDENTITY
    [InlineData(5u)]   // above ROTATE270
    [InlineData(99u)]
    public void FromCcdRotation_OutOfRange_FallsBackToLandscape(uint rotation)
    {
        Assert.Equal(MonitorOrientation.Landscape, CcdOrientation.FromCcdRotation(rotation));
    }
}
