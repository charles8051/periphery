using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Tests for the ADR-0026 snapshot helper: handle-gated metadata reads
/// that open and close the device briefly.
/// </summary>
[Collection("Camera")]
public sealed class SnapshotTests
{
    [Fact]
    public async Task ReadSnapshotAsync_ReturnsFormatsAndControls()
    {
        var snapshot = await CameraDevice.ReadSnapshotAsync(TestHelpers.CreateDeviceInfo());

        Assert.NotEmpty(snapshot.Formats);
        Assert.NotEmpty(snapshot.Controls);
    }

    [Fact]
    public async Task ReadSnapshotAsync_DisposesBackendAfterRead()
    {
        var backend = TestHelpers.InstallSingleTestBackend();

        await CameraDevice.ReadSnapshotAsync(TestHelpers.CreateDeviceInfo());

        Assert.True(backend.IsDisposed);
        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task ReadSnapshotAsync_NullDevice_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CameraDevice.ReadSnapshotAsync(null!));
    }

    [Fact]
    public async Task ReadSnapshotAsync_DoesNotModifyDeviceInfo()
    {
        var info = TestHelpers.CreateDeviceInfo();
        var originalId = info.Id;

        await CameraDevice.ReadSnapshotAsync(info);

        Assert.Equal(originalId, info.Id);
    }

    [Fact]
    public async Task GetSnapshotAsync_InstanceLevel_Works()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        var snapshot = await device.GetSnapshotAsync();

        Assert.NotEmpty(snapshot.Formats);
        Assert.NotEmpty(snapshot.Controls);
        Assert.Equal("test://camera0", snapshot.NativeEndpointId);
    }

    [Fact]
    public async Task Snapshot_ContainsExpectedFormatDetails()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        var snapshot = await device.GetSnapshotAsync();

        var hd = snapshot.Formats.FirstOrDefault(
            f => f.Width == 1920 && f.Height == 1080 && f.PixelFormat == CameraPixelFormat.Yuy2);
        Assert.NotNull(hd);
        Assert.Equal(CameraTransport.Uncompressed, hd.Transport);
        Assert.True(hd.MaxFrameRate.ToDouble() >= 15);
    }

    [Fact]
    public async Task Snapshot_ContainsExpectedControlDetails()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        var snapshot = await device.GetSnapshotAsync();

        var brightness = snapshot.Controls.FirstOrDefault(c => c.Kind == CameraControlKind.Brightness);
        Assert.NotNull(brightness);
        Assert.Equal("Brightness", brightness.Name);
        Assert.Equal(-64, brightness.MinValue);
        Assert.Equal(64, brightness.MaxValue);
        Assert.Equal(1, brightness.Step);
        Assert.Equal(0, brightness.DefaultValue);
        Assert.False(brightness.IsReadOnly);
    }
}
