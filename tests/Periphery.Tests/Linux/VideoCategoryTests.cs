using Periphery.Linux;

namespace Periphery.Tests.Linux;

/// <summary>
/// Issue #331: a <c>video4linux</c> node is a camera only if it can capture. The capability lists
/// are the <c>ID_V4L_CAPABILITIES</c> values udev's <c>v4l_id</c> records.
/// </summary>
public class VideoCategoryTests
{
    [Theory]
    [InlineData("/dev/video0", ":capture:")]                // a UVC camera's capture node
    [InlineData("/dev/video10", ":capture:video_output:")]  // v4l2loopback, exclusive_caps=0
    [InlineData("/dev/video20", ":capture:audio:tuner:")]   // vivid
    [InlineData("video0", ":capture:")]                     // a kernel uevent's DEVNAME
    [InlineData("/dev/video0", null)]                       // no udev database, as in a container
    [InlineData(null, ":capture:")]
    public void ACaptureNodeIsACamera(string? devname, string? capabilities) =>
        Assert.Equal(DeviceCategory.Camera, LinuxCategoryMap.ResolveVideoCategory(devname, capabilities));

    [Theory]
    [InlineData("/dev/video1", ":")]                  // a memory-to-memory codec, or a UVC metadata node
    [InlineData("/dev/video21", ":video_output:")]
    [InlineData("/dev/v4l-subdev0", null)]           // v4l_id's VIDIOC_QUERYCAP fails on a subdevice
    [InlineData("/dev/v4l-touch0", ":capture:touch:")]
    [InlineData("/dev/radio0", ":tuner:radio:")]
    [InlineData("/dev/vbi0", ":")]
    [InlineData("/dev/swradio0", ":tuner:")]
    public void AnythingElseIsNotACamera(string? devname, string? capabilities) =>
        Assert.Equal(DeviceCategory.All, LinuxCategoryMap.ResolveVideoCategory(devname, capabilities));
}
