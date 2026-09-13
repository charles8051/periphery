using System.Diagnostics.Metrics;
using Periphery.Camera.Testing;
using Periphery.Testing;

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

    /// <summary>Bounds a wait so a session that never gets there fails instead of hanging (ADR-0089 D5).</summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Completes once the session has armed a timer due <paramref name="due"/> after arming on
    /// <paramref name="time"/>, counting only timers armed after the first
    /// <paramref name="armedBefore"/>. The session's waits have distinct lengths (a 5 s frame
    /// timeout; 2 s stop and producer-exit budgets; a 3 s backend-dispose budget), so the length
    /// says which wait it has reached.
    /// </summary>
    internal static async Task TimerArmedAsync(
        TimerSignalingFakeTimeProvider time, TimeSpan due, int armedBefore = 0)
    {
        // Every timer is recorded before it is signalled, so re-reading the record after each
        // signal cannot miss one.
        while (!time.ArmedDueTimes.Skip(armedBefore).Contains(due))
            await time.NextTimerArmedAsync().AsTask().WaitAsync(Patience);
    }

    /// <summary>
    /// Listens to one <c>Periphery.Camera</c> instrument and completes the returned task on its
    /// first measurement. The session updates its own metrics before it records to the
    /// instrument, so the task completing means the matching <c>session.Metrics</c> value has
    /// already moved.
    /// </summary>
    /// <remarks>
    /// The listener sees every session in this test process. Use it only for an instrument no
    /// concurrently running test can move: the stall instruments move only under
    /// <see cref="BufferExhaustionPolicy.StallProducer"/>, which only tests in the serial
    /// <c>Camera</c> collection use.
    /// </remarks>
    internal static MeterListener FirstMeasurement<T>(string instrumentName, out Task<T> firstTask)
        where T : struct
    {
        var first = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstTask = first.Task;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Periphery.Camera" && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<T>((_, value, _, _) => first.TrySetResult(value));
        listener.Start();
        return listener;
    }
}
