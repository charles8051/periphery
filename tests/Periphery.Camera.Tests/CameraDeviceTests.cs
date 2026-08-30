using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

[Collection("Camera")]
public sealed class CameraDeviceTests
{
    [Fact]
    public async Task OpenAsync_ReturnsDevice_WithDeviceInfo()
    {
        var info = TestHelpers.CreateDeviceInfo();

        await using var device = await CameraDevice.OpenAsync(info);

        Assert.Same(info, device.DeviceInfo);
    }

    [Fact]
    public async Task OpenAsync_NullDevice_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CameraDevice.OpenAsync(null!));
    }

    [Fact]
    public void EmptyId_RejectedAtConstruction()
    {
        // DeviceId enforces the non-empty instance-id invariant at the value
        // boundary, so an empty id is rejected when the DeviceInfo is built —
        // OpenAsync never sees one. (Previously a string Id let an empty value
        // through to OpenAsync's own guard.)
        Assert.Throws<ArgumentException>(
            () => new DeviceInfo { Id = "", Name = "Bad" });
    }

    [Fact]
    public async Task OpenAsync_BackendOpenFails_DisposesBackend()
    {
        var backend = TestHelpers.InstallSingleTestBackend();
        backend.FaultOnOpen = new CameraAccessDeniedException(
            "Access denied", new UnauthorizedAccessException(), "test");

        await Assert.ThrowsAsync<CameraAccessDeniedException>(
            () => CameraDevice.OpenAsync(TestHelpers.CreateDeviceInfo()));

        Assert.True(backend.IsDisposed);

        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task GetFormatsAsync_ReturnsBackendFormats()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);
        var formats = await device.GetFormatsAsync();

        Assert.NotEmpty(formats);
        Assert.Contains(formats, f => f.Width == 640 && f.Height == 480);
        Assert.Contains(formats, f => f.Width == 1920 && f.Height == 1080);
    }

    [Fact]
    public async Task GetControlsAsync_ReturnsBackendControls()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);
        var controls = await device.GetControlsAsync();

        Assert.NotEmpty(controls);
        Assert.Contains(controls, c => c.Kind == CameraControlKind.Brightness);
        Assert.Contains(controls, c => c.Kind == CameraControlKind.Exposure);
    }

    [Fact]
    public async Task SetControlAsync_SetsValue()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        await device.SetControlAsync(CameraControlKind.Brightness, 32);

        Assert.Equal(32, backend.GetControlValue(CameraControlKind.Brightness));
    }

    [Fact]
    public async Task SetControlAsync_ReadOnlyControl_Throws()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        await Assert.ThrowsAsync<CameraException>(
            () => device.SetControlAsync(CameraControlKind.Gain, 100));
    }

    [Fact]
    public async Task ResetControlAsync_RestoresDefault()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);

        await device.SetControlAsync(CameraControlKind.Brightness, 32);
        await device.ResetControlAsync(CameraControlKind.Brightness);

        Assert.Equal(0, backend.GetControlValue(CameraControlKind.Brightness));
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsSnapshot()
    {
        var backend = new InMemoryCameraBackend();
        await using var device = TestHelpers.CreateDeviceWithBackend(backend);
        var snapshot = await device.GetSnapshotAsync();

        Assert.Equal("test://camera0", snapshot.NativeEndpointId);
        Assert.NotEmpty(snapshot.Formats);
        Assert.NotEmpty(snapshot.Controls);
    }

    [Fact]
    public async Task ReadSnapshotAsync_OpensAndClosesBackend()
    {
        var backend = TestHelpers.InstallSingleTestBackend();
        var snapshot = await CameraDevice.ReadSnapshotAsync(TestHelpers.CreateDeviceInfo());

        Assert.NotEmpty(snapshot.Formats);
        Assert.True(backend.IsDisposed);

        TestHelpers.InstallTestBackendFactory();
    }

    [Fact]
    public async Task DisposeAsync_DisposesBackend()
    {
        var backend = new InMemoryCameraBackend();
        var device = TestHelpers.CreateDeviceWithBackend(backend);

        await device.DisposeAsync();

        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var device = await CameraDevice.OpenAsync(TestHelpers.CreateDeviceInfo());

        await device.DisposeAsync();
        await device.DisposeAsync();
    }

    [Fact]
    public async Task Methods_AfterDispose_ThrowObjectDisposed()
    {
        var backend = new InMemoryCameraBackend();
        var device = TestHelpers.CreateDeviceWithBackend(backend);
        await device.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.GetFormatsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.GetControlsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.GetSnapshotAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => device.SetControlAsync(CameraControlKind.Brightness, 0));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => device.OpenSessionAsync(TestHelpers.DefaultConfig));
    }

    [Fact]
    public async Task NoBackendFactory_UsesNativeBackendOrThrows()
    {
        TestHelpers.ClearBackendFactory();
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                // The native backend (MF / V4L2) is auto-selected. It won't
                // find the test device, so expect CameraDeviceNotFoundException.
                await Assert.ThrowsAsync<CameraDeviceNotFoundException>(
                    () => CameraDevice.OpenAsync(TestHelpers.CreateDeviceInfo()));
            }
            else
            {
                // macOS: no native backend yet (AVFoundation planned).
                await Assert.ThrowsAsync<PlatformNotSupportedException>(
                    () => CameraDevice.OpenAsync(TestHelpers.CreateDeviceInfo()));
            }
        }
        finally
        {
            TestHelpers.InstallTestBackendFactory();
        }
    }
}
