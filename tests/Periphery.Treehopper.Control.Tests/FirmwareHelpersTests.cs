using Periphery.Treehopper.Control;
using Xunit;

namespace Periphery.Treehopper.Control.Tests;

public class FirmwareHelpersTests
{
    [Theory]
    [InlineData(273, 274, FirmwareStatus.UpdateAvailable)]
    [InlineData(274, 274, FirmwareStatus.UpToDate)]
    [InlineData(300, 274, FirmwareStatus.UpToDate)]   // newer than target is still "up to date"
    [InlineData(null, 274, FirmwareStatus.Unknown)]
    [InlineData(273, null, FirmwareStatus.Unknown)]
    public void DeriveIdle_ComparesVersionToTarget(int? version, int? target, FirmwareStatus expected)
        => Assert.Equal(expected, FirmwareView.DeriveIdle(version, target));

    [Theory]
    [InlineData("274", 274)]
    [InlineData("0x0112", 0x0112)]
    public void FirmwareVersion_TryParse_HexAndDecimal(string raw, int expected)
    {
        Assert.True(FirmwareVersion.TryParse(raw, out int v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData(null)]
    public void FirmwareVersion_TryParse_RejectsGarbage(string? raw)
        => Assert.False(FirmwareVersion.TryParse(raw, out _));

    [Fact]
    public void FirmwareVersion_Describe()
        => Assert.Equal("2.74 (code 274)", FirmwareVersion.Describe(274));
}
