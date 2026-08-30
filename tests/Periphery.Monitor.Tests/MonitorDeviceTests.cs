using Periphery.Monitor.Tests.Fakes;

namespace Periphery.Monitor.Tests;

public class MonitorDeviceTests
{
    private static DeviceInfo CreateDeviceInfo() => new()
    {
        Id = @"DISPLAY\TEST0001\1&fake&0&UID0",
        Name = "Test Monitor",
        Category = DeviceCategory.Monitor,
    };

    [Fact]
    public async Task PlaneFlags_ReflectComposedBackends()
    {
        await using var both = MonitorDevice.CreateForTest(
            CreateDeviceInfo(), new TestMonitorBackend(), new TestDisplayModeBackend());
        Assert.True(both.SupportsVcp);
        Assert.True(both.SupportsDisplayMode);

        await using var vcpOnly = MonitorDevice.CreateForTest(
            CreateDeviceInfo(), new TestMonitorBackend(), null);
        Assert.True(vcpOnly.SupportsVcp);
        Assert.False(vcpOnly.SupportsDisplayMode);
    }

    [Fact]
    public async Task AbsentPlane_ThrowsMonitorCapabilityException()
    {
        await using var vcpOnly = MonitorDevice.CreateForTest(
            CreateDeviceInfo(), new TestMonitorBackend(), null);
        await Assert.ThrowsAsync<MonitorCapabilityException>(
            () => vcpOnly.GetCurrentModeAsync());
        await Assert.ThrowsAsync<MonitorCapabilityException>(
            () => vcpOnly.SetOrientationAsync(MonitorOrientation.Portrait));

        await using var modeOnly = MonitorDevice.CreateForTest(
            CreateDeviceInfo(), null, new TestDisplayModeBackend());
        await Assert.ThrowsAsync<MonitorCapabilityException>(
            () => modeOnly.GetBrightnessAsync());
        await Assert.ThrowsAsync<MonitorCapabilityException>(
            () => modeOnly.GetCapabilitiesAsync());
    }

    [Fact]
    public async Task Brightness_NormalizesOverReportedMaximum()
    {
        var backend = new TestMonitorBackend();
        backend.Features[VcpCode.Luminance] = new VcpFeatureValue(Current: 30, Maximum: 200);
        await using var monitor = MonitorDevice.CreateForTest(CreateDeviceInfo(), backend, null);

        Assert.Equal(0.15, await monitor.GetBrightnessAsync(), precision: 10);

        await monitor.SetBrightnessAsync(0.5);
        Assert.Equal((VcpCode.Luminance, (ushort)100), backend.Writes[^1]);

        await monitor.SetBrightnessAsync(2.0); // Clamps to 1.0.
        Assert.Equal((VcpCode.Luminance, (ushort)200), backend.Writes[^1]);

        await monitor.SetBrightnessAsync(-1); // Clamps to 0.
        Assert.Equal((VcpCode.Luminance, (ushort)0), backend.Writes[^1]);
    }

    [Fact]
    public async Task Capabilities_AreFetchedOnceAndCached()
    {
        var backend = new TestMonitorBackend();
        await using var monitor = MonitorDevice.CreateForTest(CreateDeviceInfo(), backend, null);

        var first = await monitor.GetCapabilitiesAsync();
        var second = await monitor.GetCapabilitiesAsync();

        Assert.Same(first, second);
        Assert.Equal(1, backend.CapabilitiesReads);
        Assert.Equal("Fake", first.Model);
        Assert.True(first.Supports(VcpCode.Luminance));
    }

    [Fact]
    public async Task PowerAndInput_MapThroughVcpCodes()
    {
        var backend = new TestMonitorBackend();
        backend.Features[VcpCode.InputSource] = new VcpFeatureValue(0x11, 0x12);
        await using var monitor = MonitorDevice.CreateForTest(CreateDeviceInfo(), backend, null);

        Assert.Equal(MonitorPowerMode.On, await monitor.GetPowerModeAsync());
        await monitor.SetPowerModeAsync(MonitorPowerMode.SoftOff);
        Assert.Equal((VcpCode.PowerMode, (ushort)0x04), backend.Writes[^1]);

        Assert.Equal(0x11, await monitor.GetInputSourceAsync());
        await monitor.SetInputSourceAsync(MonitorInputSource.DisplayPort1);
        Assert.Equal((VcpCode.InputSource, (ushort)0x0F), backend.Writes[^1]);
    }

    [Fact]
    public async Task DisplayModePlane_ForwardsModeAndOrientation()
    {
        var mode = new TestDisplayModeBackend();
        await using var monitor = MonitorDevice.CreateForTest(CreateDeviceInfo(), null, mode);

        Assert.Equal(new DisplayMode(1920, 1080, 60), await monitor.GetCurrentModeAsync());
        Assert.Equal(3, (await monitor.GetSupportedModesAsync()).Count);

        await monitor.SetModeAsync(new DisplayMode(720, 1280, 60), persist: true);
        Assert.Equal(new DisplayMode(720, 1280, 60), mode.CurrentMode);
        Assert.True(mode.LastPersist);

        await monitor.SetOrientationAsync(MonitorOrientation.Portrait);
        Assert.Equal(MonitorOrientation.Portrait, mode.Orientation);
        Assert.False(mode.LastPersist);
    }

    [Fact]
    public async Task DisposeAsync_DisposesBothPlanes()
    {
        var vcp = new TestMonitorBackend();
        var mode = new TestDisplayModeBackend();
        var monitor = MonitorDevice.CreateForTest(CreateDeviceInfo(), vcp, mode);

        await monitor.DisposeAsync();

        Assert.True(vcp.Disposed);
        Assert.True(mode.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.GetBrightnessAsync());
    }
}
