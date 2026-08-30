using Periphery.Treehopper.Control.Cli;
using Xunit;

namespace Periphery.Treehopper.Control.Cli.Tests;

public class CliTests
{
    private static Parsed Ok(params string[] args)
    {
        var r = Cli.Parse(args);
        Assert.Null(r.Error);
        Assert.NotNull(r.Value);
        return r.Value!;
    }

    private static string Err(params string[] args)
    {
        var r = Cli.Parse(args);
        Assert.Null(r.Value);
        Assert.NotNull(r.Error);
        return r.Error!;
    }

    [Fact]
    public void NoArgs_IsList() => Assert.Equal(CommandKind.List, Ok().Kind);

    [Fact]
    public void FirmwareList_IsAliasOfList() => Assert.Equal(CommandKind.List, Ok("firmware", "list").Kind);

    [Fact]
    public void Watch_OptionalSelector()
    {
        Assert.Null(Ok("watch").Selector);
        Assert.Equal("TH-1", Ok("watch", "TH-1").Selector);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("analog")]
    public void Pin_ValidActions(string action)
    {
        var p = Ok("pin", "TH-1", "3", action);
        Assert.Equal(CommandKind.Pin, p.Kind);
        Assert.Equal("TH-1", p.Selector);
        Assert.Equal(3, p.Pin);
        Assert.Equal(action, p.PinAction);
    }

    [Fact]
    public void Pin_BadPinNumber_IsError() => Assert.Contains("Pin must be 0-19", Err("pin", "TH-1", "20", "high"));

    [Fact]
    public void Pin_BadAction_IsError() => Assert.Contains("Action must be", Err("pin", "TH-1", "3", "wiggle"));

    [Fact]
    public void Pin_MissingArgs_IsError() => Assert.Contains("Usage: treehopper pin", Err("pin", "TH-1"));

    [Fact]
    public void I2c_BareAndScanForms()
    {
        Assert.Equal("TH-1", Ok("i2c", "TH-1").Selector);
        Assert.Equal("TH-1", Ok("i2c", "scan", "TH-1").Selector);
        Assert.Equal(CommandKind.I2c, Ok("i2c", "TH-1").Kind);
    }

    [Fact]
    public void I2c_MissingSelector_IsError() => Assert.Contains("Usage: treehopper i2c", Err("i2c"));

    [Fact]
    public void FirmwareAll_AndBoard()
    {
        Assert.Equal(CommandKind.FirmwareAll, Ok("firmware", "all").Kind);
        var board = Ok("firmware", "board", "TH-9");
        Assert.Equal(CommandKind.FirmwareBoard, board.Kind);
        Assert.Equal("TH-9", board.Selector);
    }

    [Fact]
    public void FirmwareBoard_MissingSelector_IsError() => Assert.Contains("Usage: treehopper firmware board", Err("firmware", "board"));

    [Fact]
    public void Firmware_UnknownSub_IsError() => Assert.Contains("Unknown firmware subcommand", Err("firmware", "wibble"));

    [Fact]
    public void Flags_Parsed()
    {
        var p = Ok("firmware", "all", "--yes", "--force", "--json");
        Assert.True(p.Yes);
        Assert.True(p.Force);
        Assert.True(p.Json);
    }

    [Fact]
    public void File_TakesPath() => Assert.Equal("fw.tfi", Ok("firmware", "all", "--file", "fw.tfi").FilePath);

    [Fact]
    public void File_MissingValue_IsError() => Assert.Contains("--file requires", Err("firmware", "all", "--file"));

    [Theory]
    [InlineData("274", 274)]
    [InlineData("0x0112", 0x0112)]
    public void TargetVersion_HexOrDecimal(string raw, int expected)
    {
        Assert.Equal(expected, Ok("list", "--target-version", raw).TargetVersion);
        Assert.Equal(expected, Ok("list", "--target", raw).TargetVersion);
    }

    [Fact]
    public void TargetVersion_Invalid_IsError() => Assert.Contains("Invalid --target-version", Err("list", "--target-version", "nope"));

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void Seconds_NonPositive_IsError(string value) => Assert.Contains("--seconds must be", Err("watch", "--seconds", value));

    [Fact]
    public void Seconds_Valid() => Assert.Equal(5, Ok("watch", "--seconds", "5").Seconds);

    [Fact]
    public void UnknownOption_IsError() => Assert.Contains("Unknown option", Err("--nope"));

    [Fact]
    public void UnknownCommand_IsError() => Assert.Contains("Unknown command", Err("frobnicate"));

    [Fact]
    public void Help_And_Version_ShortCircuit()
    {
        Assert.Equal(CommandKind.Help, Ok("-h").Kind);
        Assert.Equal(CommandKind.Help, Ok("--help").Kind);
        Assert.Equal(CommandKind.Version, Ok("--version").Kind);
    }
}
