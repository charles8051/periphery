using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Periphery.Camera.Internal;
using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

/// <summary>
/// Issue #123: a camera teardown step that overruns its budget is abandoned to a
/// background thread that may still hold the device. These tests pin the three
/// things that make the abandonment observable — a metric, a log, and a
/// fail-fast refusal of the next open — and the two orderings the fix depends
/// on: the refusal lifts when the abandoned work completes, and a healthy device
/// is never refused.
/// </summary>
[Collection("Camera")]
public sealed class AbandonedTeardownTests : IDisposable
{
    private const string MeterName = "Periphery.Camera";

    public AbandonedTeardownTests() => PendingTeardowns.Clear();

    // Drop any registrations this test left behind, so the process-global
    // registry does not leak a wedged device into the next test.
    public void Dispose() => PendingTeardowns.Clear();

    [Fact]
    public async Task StopThatOverrunsIsCounted_AndReopenIsRefusedNamingTheDevice()
    {
        var abandoned = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardowns_abandoned", v => Interlocked.Add(ref abandoned, v));

        var time = new FakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WEDGED");
        // The backend's StopCaptureAsync parks on the gate forever — a driver
        // whose Flush never returns.
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };

        try
        {
            var session = await TestHelpers.CreateSessionWithBackend(backend, device: device, timeProvider: time);
            await session.StartCaptureAsync();
            using (var frame = await session.ReadFrameAsync()) { }

            // Dispose races the abandoned stop; advancing past the 2s budget trips it.
            await DisposeWithClockAdvanceAsync(session, time);

            Assert.Equal(1, Interlocked.Read(ref abandoned));

            // The next open of the SAME device is refused, naming it and the step.
            var ex = await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraDevice.OpenAsync(device));
            Assert.Equal(device.Id, ex.DeviceId);
            Assert.Contains(BoundedTeardown.Steps.StopCapture, ex.Message);
            Assert.False(ex.Completion.IsCompleted);
        }
        finally
        {
            stopGate.TrySetResult();
        }
    }

    [Fact]
    public async Task ReopenSucceedsOnceTheAbandonedWorkCompletes()
    {
        var time = new FakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\RECOVERS");
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };

        var session = await TestHelpers.CreateSessionWithBackend(backend, device: device, timeProvider: time);
        await session.StartCaptureAsync();
        using (var frame = await session.ReadFrameAsync()) { }
        await DisposeWithClockAdvanceAsync(session, time);

        // Refused while the stop is still parked.
        var pending = await Assert.ThrowsAsync<CameraTeardownPendingException>(
            () => CameraDevice.OpenAsync(device));

        // Let the wedged driver return. The registry entry clears, and the
        // exception's Completion is the signal a waiting caller uses.
        stopGate.SetResult();
        await pending.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(PendingTeardowns.Find(device.Id));

        // A fresh open of the same device now goes through.
        using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
        await using var reopened = await CameraDevice.OpenAsync(device);
        Assert.Equal(device.Id, reopened.DeviceInfo.Id);
    }

    [Fact]
    public async Task ADifferentDeviceIsNotRefused()
    {
        var time = new FakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wedged = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WEDGED");
        var healthy = TestHelpers.CreateDeviceInfo("TEST\\CAM\\HEALTHY");
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };

        try
        {
            var session = await TestHelpers.CreateSessionWithBackend(backend, device: wedged, timeProvider: time);
            await session.StartCaptureAsync();
            using (var frame = await session.ReadFrameAsync()) { }
            await DisposeWithClockAdvanceAsync(session, time);

            Assert.NotNull(PendingTeardowns.Find(wedged.Id));

            using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
            await using var other = await CameraDevice.OpenAsync(healthy);
            Assert.Equal(healthy.Id, other.DeviceInfo.Id);
        }
        finally
        {
            stopGate.TrySetResult();
        }
    }

    [Fact]
    public async Task CleanDisposalRegistersNothing_AndDoesNotRefuseReopen()
    {
        var abandoned = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardowns_abandoned", v => Interlocked.Add(ref abandoned, v));

        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\CLEAN");
        var backend = new InMemoryCameraBackend();

        var session = await TestHelpers.CreateSessionWithBackend(backend, device: device);
        await session.StartCaptureAsync();
        using (var frame = await session.ReadFrameAsync()) { }
        await session.DisposeAsync();

        Assert.Equal(0, Interlocked.Read(ref abandoned));
        Assert.Null(PendingTeardowns.Find(device.Id));

        // Reopen is not refused.
        using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
        await using var reopened = await CameraDevice.OpenAsync(device);
        Assert.Equal(device.Id, reopened.DeviceInfo.Id);
    }

    [Fact]
    public async Task BackendDisposalThatOverrunsIsAbandoned_AndRefusesReopen()
    {
        var abandoned = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardowns_abandoned", v => Interlocked.Add(ref abandoned, v));

        var time = new FakeTimeProvider();
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\DISPOSEWEDGE");
        // Stop returns fine; the backend's own disposal (Shutdown/close) is what wedges.
        var backend = new InMemoryCameraBackend { BlockDisposeUntil = disposeGate.Task };

        try
        {
            var session = await TestHelpers.CreateSessionWithBackend(backend, device: device, timeProvider: time);
            await session.StartCaptureAsync();
            using (var frame = await session.ReadFrameAsync()) { }
            await DisposeWithClockAdvanceAsync(session, time);

            Assert.Equal(1, Interlocked.Read(ref abandoned));
            var ex = await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraDevice.OpenAsync(device));
            Assert.Contains(BoundedTeardown.Steps.BackendDispose, ex.Message);
        }
        finally
        {
            disposeGate.TrySetResult();
        }
    }

    // Review finding: the exception's Completion must cover a step abandoned
    // after the exception was built, not just the step present when it was.
    [Fact]
    public async Task ExceptionCompletion_WaitsForAStepAbandonedAfterItWasBuilt()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\OVERLAP";
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // First step abandons; the exception is built from that snapshot.
        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, stopGate.Task, clock);
        var ex = PendingTeardowns.Find(id)!.ToException();

        // A second step abandons before the first completes.
        PendingTeardowns.Register(id, BoundedTeardown.Steps.ProducerExit, producerGate.Task, clock);

        // Completing only the first step must not clear the signal.
        stopGate.SetResult();
        await Task.Delay(50);
        Assert.False(ex.Completion.IsCompleted);

        // The signal clears only once the later step completes too.
        producerGate.SetResult();
        await ex.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(PendingTeardowns.Find(id));
    }

    // Review finding: a teardown that registers between the pre-check and the
    // native open must still refuse the open, and dispose the backend it opened.
    [Fact]
    public async Task OpenThatRacesARegisteringTeardown_IsRefused_AndDisposesTheBackend()
    {
        var clock = new FakeTimeProvider();
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\RACE");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterOnOpenBackend? captured = null;

        var previous = CameraDevice.BackendFactory;
        CameraDevice.BackendFactory = _ =>
        {
            captured = new RegisterOnOpenBackend(device.Id, pending.Task, clock);
            return captured;
        };
        try
        {
            var ex = await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraDevice.OpenAsync(device));
            Assert.Equal(device.Id, ex.DeviceId);
            Assert.NotNull(captured);
            Assert.True(captured!.InnerDisposed, "the backend opened during the race must be disposed");
        }
        finally
        {
            CameraDevice.BackendFactory = previous;
            pending.TrySetResult();
        }
    }

    // Review finding (media-foundation-lifetime), at the CameraDevice layer: a
    // failed open disposes the backend through the bounded path and leaves no
    // pending entry behind.
    [Fact]
    public async Task FailedOpen_DisposesTheBackend_AndRegistersNoPending()
    {
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\OPENFAIL");
        var backend = new InMemoryCameraBackend { FaultOnOpen = new CameraException("open boom", device.Id) };
        using var scope = CameraTestScope.Install(backend);

        await Assert.ThrowsAsync<CameraException>(() => CameraDevice.OpenAsync(device));

        Assert.True(backend.IsDisposed);
        Assert.Null(PendingTeardowns.Find(device.Id));
    }

    // Runs DisposeAsync on a background task and advances the fake clock past the
    // teardown budgets until it completes, mirroring the pattern in
    // CameraSessionClockTests: virtual time drives the abandon, real yields let
    // the continuations run.
    private static async Task DisposeWithClockAdvanceAsync(CameraSession session, FakeTimeProvider time)
    {
        var dispose = Task.Run(async () => await session.DisposeAsync());
        var beyondBudget = BoundedTeardown.BackendDisposeBudget + TimeSpan.FromSeconds(1);

        for (int i = 0; i < 500 && !dispose.IsCompleted; i++)
        {
            time.Advance(beyondBudget);
            await Task.Delay(10);
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static MeterListener StartListener<T>(string instrumentName, Action<T> onMeasurement)
        where T : struct
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<T>((_, value, _, _) => onMeasurement(value));
        listener.Start();
        return listener;
    }

    // A backend that registers a pending teardown for its device *during* the
    // open, modelling a previous session's teardown that abandons in the gap
    // between the pre-check and the native open. Delegates everything else to a
    // real InMemoryCameraBackend so the open otherwise succeeds.
    private sealed class RegisterOnOpenBackend : ICameraBackend
    {
        private readonly InMemoryCameraBackend _inner = new();
        private readonly ICameraBackend _io;
        private readonly string _deviceId;
        private readonly Task _pending;
        private readonly TimeProvider _clock;

        public RegisterOnOpenBackend(string deviceId, Task pending, TimeProvider clock)
        {
            _io = _inner;
            _deviceId = deviceId;
            _pending = pending;
            _clock = clock;
        }

        public bool InnerDisposed => _inner.IsDisposed;

        public string NativeEndpointId => _io.NativeEndpointId;

        public async Task OpenAsync(CancellationToken ct)
        {
            await _io.OpenAsync(ct).ConfigureAwait(false);
            PendingTeardowns.Register(_deviceId, BoundedTeardown.Steps.StopCapture, _pending, _clock);
        }

        public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct) => _io.GetFormatsAsync(ct);
        public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct) => _io.GetControlsAsync(ct);
        public Task<CameraControlState?> GetControlAsync(CameraControlKind control, CancellationToken ct) => _io.GetControlAsync(control, ct);
        public Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct) => _io.SetControlAsync(control, value, ct);
        public Task ResetControlAsync(CameraControlKind control, CancellationToken ct) => _io.ResetControlAsync(control, ct);
        public Task ConfigureAsync(CameraConfiguration configuration, CancellationToken ct) => _io.ConfigureAsync(configuration, ct);
        public Task StartCaptureAsync(CancellationToken ct) => _io.StartCaptureAsync(ct);
        public Task<RawCameraFrame> ReadRawFrameAsync(CancellationToken ct) => _io.ReadRawFrameAsync(ct);
        public Task StopCaptureAsync() => _io.StopCaptureAsync();
        public ValueTask DisposeAsync() => _io.DisposeAsync();
    }
}
