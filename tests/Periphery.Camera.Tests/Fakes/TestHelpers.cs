using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests.Fakes;

[CollectionDefinition("Camera")]
public sealed class CameraTestCollection : ICollectionFixture<CameraTestFixture> { }

public sealed class CameraTestFixture : IDisposable
{
    public CameraTestFixture() => TestHelpers.InstallTestBackendFactory();
    public void Dispose() => TestHelpers.ClearBackendFactory();
}

/// <summary>
/// Thin adapters over the shipped <c>Periphery.Camera.Testing</c> package
/// (<see cref="InMemoryCameraBackend"/> / <see cref="CameraTestHarness"/>), so
/// the camera suite dogfoods the public seam (ADR-0065) rather than keeping a
/// second, private fake. New tests may use the package types directly.
/// </summary>
internal static class TestHelpers
{
    internal static readonly CameraFormat DefaultFormat = CameraTestFormats.Vga;
    internal static readonly CameraFormat HdFormat = CameraTestFormats.Hd1080;
    internal static readonly CameraConfiguration DefaultConfig = new(DefaultFormat);

    internal static DeviceInfo CreateDeviceInfo(string id = "TEST\\CAM\\0001") =>
        CameraTestFormats.CreateDeviceInfo(id);

    /// <summary>
    /// Install a factory that mints a <em>fresh</em> backend per open — the
    /// faithful shape, and the only one that works on paths which open more than
    /// once (notably <c>CameraSession.For(device).OpenAsync()</c>, which does a
    /// snapshot open before the capture open).
    /// </summary>
    internal static void InstallTestBackendFactory(
        string nativeId = "test://camera0",
        List<CameraFormat>? formats = null,
        List<CameraControlInfo>? controls = null) =>
        CameraDevice.BackendFactory = _ => new InMemoryCameraBackend(nativeId, formats, controls);

    /// <summary>
    /// Install one shared backend so the test can inspect it afterwards. Only
    /// valid for single-open paths — an <see cref="InMemoryCameraBackend"/> models
    /// one device lifecycle and throws once disposed.
    /// </summary>
    internal static InMemoryCameraBackend InstallSingleTestBackend(
        string nativeId = "test://camera0",
        List<CameraFormat>? formats = null,
        List<CameraControlInfo>? controls = null)
    {
        var backend = new InMemoryCameraBackend(nativeId, formats, controls);
        CameraDevice.BackendFactory = _ => backend;
        return backend;
    }

    internal static void ClearBackendFactory() => CameraDevice.BackendFactory = null;

    internal static CameraDevice CreateDeviceWithBackend(
        InMemoryCameraBackend? backend = null, DeviceInfo? device = null) =>
        CameraTestHarness
            .OpenDeviceAsync(backend ?? new InMemoryCameraBackend(), device)
            .GetAwaiter()
            .GetResult();

    internal static Task<CameraSession> CreateSessionWithBackend(
        InMemoryCameraBackend? backend = null,
        DeviceInfo? device = null,
        CameraConfiguration? config = null,
        CameraSessionOptions? options = null,
        TimeProvider? timeProvider = null) =>
        CameraTestHarness.OpenSessionAsync(
            backend ?? new InMemoryCameraBackend(),
            config ?? DefaultConfig,
            device,
            options,
            timeProvider);
}
