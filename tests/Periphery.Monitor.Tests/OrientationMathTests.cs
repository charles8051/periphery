namespace Periphery.Monitor.Tests;

public class OrientationMathTests
{
    [Theory]
    [InlineData(MonitorOrientation.Landscape, MonitorOrientation.Portrait, true)]
    [InlineData(MonitorOrientation.Landscape, MonitorOrientation.PortraitFlipped, true)]
    [InlineData(MonitorOrientation.Landscape, MonitorOrientation.LandscapeFlipped, false)]
    [InlineData(MonitorOrientation.Portrait, MonitorOrientation.PortraitFlipped, false)]
    [InlineData(MonitorOrientation.Portrait, MonitorOrientation.Landscape, true)]
    [InlineData(MonitorOrientation.PortraitFlipped, MonitorOrientation.LandscapeFlipped, true)]
    [InlineData(MonitorOrientation.Landscape, MonitorOrientation.Landscape, false)]
    public void SwapsDimensions_OnlyAcrossTheLandscapePortraitBoundary(
        MonitorOrientation from, MonitorOrientation to, bool expected)
    {
        Assert.Equal(expected, OrientationMath.SwapsDimensions(from, to));
    }

    [Fact]
    public void Reframe_SwapsAcrossBoundary_PreservesOtherwise()
    {
        Assert.Equal((1280, 720), OrientationMath.Reframe(
            720, 1280, MonitorOrientation.Portrait, MonitorOrientation.Landscape));
        Assert.Equal((720, 1280), OrientationMath.Reframe(
            1280, 720, MonitorOrientation.Landscape, MonitorOrientation.Portrait));
        Assert.Equal((1280, 720), OrientationMath.Reframe(
            1280, 720, MonitorOrientation.Landscape, MonitorOrientation.LandscapeFlipped));
    }
}
