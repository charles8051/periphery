using Periphery.Cli.Commands;

namespace Periphery.Cli.Tests;

/// <summary>
/// The pure selection rule behind <c>--id</c> / <c>--name</c> on the monitor
/// and camera commands.
/// </summary>
public sealed class DeviceSelectionTests
{
    private static readonly DeviceInfo Front = new() { Id = "USB\\VID_046D&PID_0825\\1", Name = "Front Camera" };
    private static readonly DeviceInfo Rear = new() { Id = "USB\\VID_046D&PID_0825\\2", Name = "Rear Camera" };
    private static readonly DeviceInfo Ir = new() { Id = "USB\\VID_1234&PID_5678\\1", Name = "IR Sensor" };

    private static DeviceSelection.Result Select(
        IReadOnlyList<DeviceInfo> candidates, string? id = null, string? name = null)
        => DeviceSelection.Select(candidates, "camera", "periphery devices list --category Camera", id, name);

    [Fact]
    public void Id_MatchesCaseInsensitively_AndWinsOverName()
    {
        var result = Select([Front, Rear], id: "usb\\vid_046d&pid_0825\\2", name: "Front");

        Assert.Same(Rear, result.Device);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Id_WithNoMatch_NamesTheListCommand()
    {
        var result = Select([Front], id: "USB\\NOPE");

        Assert.Null(result.Device);
        Assert.Equal(
            "No camera with Id 'USB\\NOPE'. Run `periphery devices list --category Camera` for the connected set.",
            result.Error);
    }

    [Fact]
    public void Name_UniqueSubstring_Selects()
    {
        var result = Select([Front, Rear, Ir], name: "ir sen");

        Assert.Same(Ir, result.Device);
    }

    [Fact]
    public void Name_WithNoMatch_IsAnError()
    {
        var result = Select([Front, Rear], name: "Webcam");

        Assert.Null(result.Device);
        Assert.Equal("No camera name contains 'Webcam'.", result.Error);
    }

    [Fact]
    public void Name_MatchingSeveral_IsAnError()
    {
        var result = Select([Front, Rear, Ir], name: "camera");

        Assert.Null(result.Device);
        Assert.Equal("'camera' matches 2 cameras — narrow it or use --id.", result.Error);
    }

    [Fact]
    public void NoFilter_SingleDevice_IsSelected()
    {
        Assert.Same(Front, Select([Front]).Device);
    }

    [Fact]
    public void NoFilter_NoDevices_IsAnError()
    {
        var result = Select([]);

        Assert.Null(result.Device);
        Assert.Equal("No cameras enumerated.", result.Error);
    }

    [Fact]
    public void NoFilter_SeveralDevices_AsksForAFilter()
    {
        var result = Select([Front, Rear]);

        Assert.Null(result.Device);
        Assert.Equal(
            "2 cameras connected — pick one with --name or --id (see `periphery devices list --category Camera`).",
            result.Error);
    }
}
