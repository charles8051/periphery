using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Periphery.Camera.Internal;
using Periphery.Camera.Testing;
using Periphery.Camera.Tests.Fakes;
using Periphery.Testing;

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
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WEDGED");
        var abandoned = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardowns_abandoned", v => Interlocked.Add(ref abandoned, v));

        var time = new TimerSignalingFakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The backend's StopCaptureAsync parks on the gate forever — a driver
        // whose Flush never returns.
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };

        try
        {
            var session = await TestHelpers.CreateSessionWithBackend(backend, device: device, timeProvider: time);
            await session.StartCaptureAsync();
            using (var frame = await session.ReadFrameAsync()) { }

            // Dispose races the abandoned stop; advancing past the 2s budget trips it.
            await DisposeAbandoningAsync(session, time, BoundedTeardown.StopCaptureBudget);

            // One abandonment, counted once. The registry says which step, on this
            // device; the meter says the counter fired exactly once for it.
            Assert.Equal(1, Interlocked.Read(ref abandoned));
            Assert.Equal(
                [BoundedTeardown.Steps.StopCapture],
                PendingTeardowns.Find(device.Id)!.Steps);

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
        var time = new TimerSignalingFakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\RECOVERS");
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };

        var session = await TestHelpers.CreateSessionWithBackend(backend, device: device, timeProvider: time);
        await session.StartCaptureAsync();
        using (var frame = await session.ReadFrameAsync()) { }
        await DisposeAbandoningAsync(session, time, BoundedTeardown.StopCaptureBudget);

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
        var time = new TimerSignalingFakeTimeProvider();
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wedged = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WEDGED");
        var healthy = TestHelpers.CreateDeviceInfo("TEST\\CAM\\HEALTHY");
        var backend = new InMemoryCameraBackend { BlockStopUntil = stopGate.Task };

        try
        {
            var session = await TestHelpers.CreateSessionWithBackend(backend, device: wedged, timeProvider: time);
            await session.StartCaptureAsync();
            using (var frame = await session.ReadFrameAsync()) { }
            await DisposeAbandoningAsync(session, time, BoundedTeardown.StopCaptureBudget);

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
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\CLEAN");
        var abandoned = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardowns_abandoned", v => Interlocked.Add(ref abandoned, v));

        var backend = new InMemoryCameraBackend();

        var session = await TestHelpers.CreateSessionWithBackend(backend, device: device);
        await session.StartCaptureAsync();
        using (var frame = await session.ReadFrameAsync()) { }
        await session.DisposeAsync();

        // The zero is the only thing in the suite that fails when the counter is
        // moved to a path every bounded step reaches, abandoned or not.
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
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\DISPOSEWEDGE");
        var abandoned = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardowns_abandoned", v => Interlocked.Add(ref abandoned, v));

        var time = new TimerSignalingFakeTimeProvider();
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Stop returns fine; the backend's own disposal (Shutdown/close) is what wedges.
        var backend = new InMemoryCameraBackend { BlockDisposeUntil = disposeGate.Task };

        try
        {
            var session = await TestHelpers.CreateSessionWithBackend(backend, device: device, timeProvider: time);
            await session.StartCaptureAsync();
            using (var frame = await session.ReadFrameAsync()) { }
            await DisposeAbandoningAsync(session, time, BoundedTeardown.BackendDisposeBudget);

            Assert.Equal(1, Interlocked.Read(ref abandoned));
            Assert.Equal(
                [BoundedTeardown.Steps.BackendDispose],
                PendingTeardowns.Find(device.Id)!.Steps);

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
        // Continuations run inline: completed from Task.Run, where there is no
        // SynchronizationContext, a gate returns from SetResult only after the registry and the
        // exception's Completion have both reacted to it.
        var stopGate = new TaskCompletionSource();
        var producerGate = new TaskCompletionSource();

        // First step abandons; the exception is built from that snapshot.
        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, stopGate.Task, clock);
        var ex = PendingTeardowns.Find(id)!.ToException();

        // A second step abandons before the first completes.
        PendingTeardowns.Register(id, BoundedTeardown.Steps.ProducerExit, producerGate.Task, clock);

        // Completing only the first step must not clear the signal.
        await Task.Run(() => stopGate.SetResult());
        Assert.False(ex.Completion.IsCompleted);

        // The signal clears only once the later step completes too. Asserted the same way, so it
        // also shows the assertion above ran after the continuations did.
        await Task.Run(() => producerGate.SetResult());
        Assert.True(ex.Completion.IsCompleted, "Completion did not react inline to the last step completing");
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

    // Disposes the session and expires exactly one budget: the wedged step's. Teardown steps run
    // one after another, so when that step's budget timer is armed it is the only budget pending,
    // and advancing by that budget cannot expire a later step's. The previous helper advanced past
    // every budget each 10 ms of real time, which abandoned a later step that was merely slow
    // (#256).
    private static async Task DisposeAbandoningAsync(
        CameraSession session, TimerSignalingFakeTimeProvider time, TimeSpan wedgedBudget)
    {
        int armedBefore = time.ArmedDueTimes.Count;
        var dispose = session.DisposeAsync().AsTask();

        await TestHelpers.TimerArmedAsync(time, wedgedBudget, armedBefore);
        time.Advance(wedgedBudget);

        await dispose.WaitAsync(TestHelpers.Patience);
    }

    /// <summary>
    /// Listens to one instrument on the process-global camera meter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counts asserted through this are exact, and stay exact. #221 item 4
    /// proposed relaxing them after one Linux CI failure (a 2 where a test asserts
    /// 1), on the premise that another test's abandonment emits into this
    /// listener's window from a background thread. That premise does not hold here.
    /// <c>TeardownsAbandoned.Add</c> is emitted synchronously inside
    /// <see cref="BoundedTeardown"/>'s abandon path, which every caller awaits, and
    /// <c>AssemblyInfo.cs</c> has disabled parallelization for this assembly since
    /// before the failure was seen, so no other test is running to contaminate it.
    /// The instrument that does emit from an uncontrolled background thread is
    /// <c>AbandonedTeardownDuration</c>, which no test here listens to.
    /// </para>
    /// <para>
    /// Relaxing to a lower bound was tried and reverted: it let a counter that
    /// double-reports every abandonment, and one that fires on clean teardowns too,
    /// both pass. Those are the mutations an exact count exists to catch, and they
    /// leave <see cref="PendingTeardowns"/> correct, so the registry assertions
    /// beside these do not cover them.
    /// </para>
    /// <para>
    /// The cause of that one failure is therefore still unidentified. What the
    /// tests gained instead is the <c>Steps</c> assertion on the registry: if a
    /// second step really is being abandoned on the same device, a recurrence now
    /// names which steps rather than reporting that 2 is not 1.
    /// </para>
    /// </remarks>
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
