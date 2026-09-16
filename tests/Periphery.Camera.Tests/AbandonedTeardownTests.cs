using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
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
        var pending = PendingTeardowns.Find(id)!;
        var ex = pending.ToException(pending.SinceLastAbandoned);

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

    // ── Refusal window (issue #221 item 1) ───────────────────────────
    //
    // A refusal used to have no expiry, so a step whose native call never
    // returned locked its device out for the life of the process: the reported
    // case ran 6,989 consecutive refused opens over 14h33m. These pin that the
    // refusal ends and the record does not.

    [Fact]
    public void TheRefusalBoundsAreWhatTheyAreDocumentedToBe()
    {
        // Every other test here advances relative to these, so both could be
        // changed to six hours without turning any of them red -- which is the
        // shape #221 item 1 measured, a lockout that outlived the working day.
        // The numbers are policy, so they are pinned where a change to them has to
        // be deliberate.
        Assert.Equal(TimeSpan.FromSeconds(60), PendingTeardowns.RefusalWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), PendingTeardowns.RefusalCeiling);
        Assert.True(PendingTeardowns.RefusalCeiling > PendingTeardowns.RefusalWindow);
    }

    [Fact]
    public void RefusalHolds_UntilTheWindowExpires()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\HOLD";
        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, new TaskCompletionSource().Task, clock);

        // Right up to the boundary the device is still refused.
        clock.Advance(PendingTeardowns.RefusalWindow - TimeSpan.FromMilliseconds(1));
        Assert.Throws<CameraTeardownPendingException>(() => PendingTeardowns.ThrowIfPending(id));

        // On the boundary it is not.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        PendingTeardowns.ThrowIfPending(id);
    }

    [Fact]
    public async Task PastTheWindow_TheOpenIsAllowedThrough()
    {
        var clock = new FakeTimeProvider();
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WINDOW\\OPEN");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTeardowns.Register(device.Id, BoundedTeardown.Steps.BackendDispose, gate.Task, clock);

        try
        {
            // Inside the window: refused, as before.
            await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraDevice.OpenAsync(device));

            clock.Advance(PendingTeardowns.RefusalWindow);

            using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
            await using var reopened = await CameraDevice.OpenAsync(device);
            Assert.Equal(device.Id, reopened.DeviceInfo.Id);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task AdmittingPastTheWindow_IsCountedOncePerOpen()
    {
        var admitted = 0L;
        using var listener = StartListener<long>(
            "periphery.camera.teardown_refusals_expired", v => Interlocked.Add(ref admitted, v));

        var clock = new FakeTimeProvider();
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WINDOW\\COUNT");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTeardowns.Register(device.Id, BoundedTeardown.Steps.BackendDispose, gate.Task, clock);

        try
        {
            // A refusal is not an admission.
            await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraDevice.OpenAsync(device));
            Assert.Equal(0, Interlocked.Read(ref admitted));

            clock.Advance(PendingTeardowns.RefusalWindow);

            using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
            await using var reopened = await CameraDevice.OpenAsync(device);

            // One open, one count. This path checks the registry twice, an
            // admission then a recheck, and only the admission records. The
            // snapshot pass a builder adds is a third check and a second
            // admission, which is why the counter counts native opens rather than
            // caller requests.
            Assert.Equal(1, Interlocked.Read(ref admitted));

            // finding: the record has to outlive the refusal HERE, on the path
            // production takes, not only when ThrowIfPending is driven directly.
            // Dropping the entry on admission would tell a caller awaiting
            // Completion that the driver is free while the call is still parked,
            // and would silence every later admission.
            Assert.NotNull(PendingTeardowns.Find(device.Id));
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public void TheRecordOutlivesTheRefusal()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\RECORD";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTeardowns.Register(id, BoundedTeardown.Steps.ProducerExit, gate.Task, clock);

        clock.Advance(PendingTeardowns.RefusalWindow);
        PendingTeardowns.ThrowIfPending(id);

        // Expiry stops the refusal and keeps the record: the diagnostic is the
        // part of #123 that cost the time, and a caller that chose to wait for
        // the teardown rather than race it must still be able to.
        var pending = PendingTeardowns.Find(id);
        Assert.NotNull(pending);
        Assert.Equal([BoundedTeardown.Steps.ProducerExit], pending.Steps);
        var cleared = PendingTeardowns.WhenClearedAsync(id);
        Assert.False(cleared.IsCompleted);

        gate.SetResult();
        Assert.True(cleared.Wait(TestHelpers.Patience));
        Assert.Null(PendingTeardowns.Find(id));
    }

    [Fact]
    public void ANewlyAbandonedStep_GetsItsOwnWindow_EvenBehindAnExpiredOne()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\SECONDSTEP";
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, first.Task, clock);
        clock.Advance(PendingTeardowns.RefusalWindow);

        // The first step has outlived its window, so the device is admitted.
        PendingTeardowns.ThrowIfPending(id);

        // Now a second step wedges. Anchoring the window to the FIRST abandonment
        // would make this composite born expired: a call parked for zero seconds
        // would get no refusal at all, because an older one on the same device had
        // already aged out. The window is a claim about one parked call's age.
        PendingTeardowns.Register(id, BoundedTeardown.Steps.BackendDispose, second.Task, clock);

        var ex = Assert.Throws<CameraTeardownPendingException>(() => PendingTeardowns.ThrowIfPending(id));
        Assert.Contains(BoundedTeardown.Steps.BackendDispose, ex.Message);

        // And it is a full window, not the remainder of the first step's.
        clock.Advance(PendingTeardowns.RefusalWindow - TimeSpan.FromMilliseconds(1));
        Assert.Throws<CameraTeardownPendingException>(() => PendingTeardowns.ThrowIfPending(id));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        PendingTeardowns.ThrowIfPending(id);

        first.SetResult();
        second.SetResult();
    }

    [Fact]
    public void TheRefusalMessage_NeverReportsATimeAlreadyElapsed()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\REMAINING";
        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, new TaskCompletionSource().Task, clock);

        // TWO steps, and the second one is the point. With one step the two
        // anchors coincide, so the message cannot disagree with the decision even
        // if it is built from the wrong one. A message computed from PendingFor
        // here advertises "0s" immediately and goes negative a minute later, while
        // the refusal it is attached to still stands.
        clock.Advance(PendingTeardowns.RefusalWindow);
        PendingTeardowns.Register(id, BoundedTeardown.Steps.BackendDispose, new TaskCompletionSource().Task, clock);

        clock.Advance(PendingTeardowns.RefusalWindow - TimeSpan.FromSeconds(1));
        var ex = Assert.Throws<CameraTeardownPendingException>(() => PendingTeardowns.ThrowIfPending(id));

        Assert.Contains("lifts on its own in 1s", ex.Message);
        Assert.DoesNotContain("in 0s", ex.Message);
        Assert.DoesNotContain("in -", ex.Message);

        // And it reports the age from the FIRST step, which is the number a caller
        // watches grow and the one the field report sampled.
        Assert.Contains("overran its budget 119.0s ago", ex.Message);
    }

    [Fact]
    public async Task AnOpenAdmittedPastTheWindow_IsStillRefusedWhenATeardownRegistersDuringIt()
    {
        // The interleaving a reviewer raised against the window: admission samples
        // an expired entry and returns, and a fresh teardown registers on the same
        // device before the open finishes. Admission is a read rather than a
        // reservation, by a decision predating this change, so the recheck after
        // the native work is what closes it -- and it closes here because the
        // window runs from the newest abandoned step, so the composite the recheck
        // sees is seconds old rather than inheriting the expired one's age.
        var clock = new FakeTimeProvider();
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WINDOW\\RACE");
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterOnOpenBackend? captured = null;

        // An entry past its window: this open is admitted, not refused.
        PendingTeardowns.Register(device.Id, BoundedTeardown.Steps.StopCapture, stale.Task, clock);
        // Clear of the boundary on purpose: sitting exactly on it would make an
        // off-by-one in the comparison fail this test at the admission instead of
        // at the recheck, which is not what it is about.
        clock.Advance(PendingTeardowns.RefusalWindow + TimeSpan.FromSeconds(1));

        var previous = CameraDevice.BackendFactory;
        CameraDevice.BackendFactory = _ =>
        {
            captured = new RegisterOnOpenBackend(device.Id, fresh.Task, clock);
            return captured;
        };
        try
        {
            var ex = await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraDevice.OpenAsync(device));
            Assert.Equal(device.Id, ex.DeviceId);
            Assert.True(captured!.InnerDisposed, "the backend opened during the race must be disposed");
        }
        finally
        {
            CameraDevice.BackendFactory = previous;
            stale.TrySetResult();
            fresh.TrySetResult();
        }
    }

    [Fact]
    public void RepeatedAbandonment_CannotHoldTheRefusalPastTheCeiling()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\CEILING";
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, first.Task, clock);
        clock.Advance(PendingTeardowns.RefusalCeiling);

        // A step wedging now re-arms the window, and before the ceiling that is
        // right -- ANewlyAbandonedStep_GetsItsOwnWindow_EvenBehindAnExpiredOne
        // pins it. Past the ceiling it must not, or a caller that keeps wedging on
        // the same driver refuses itself forever: its own retry abandons a step,
        // which restarts the window, at any backoff. CameraSessionBuilder makes
        // that the ordinary case rather than a contrived one, because it opens
        // twice and the snapshot pass wedges milliseconds before the capture open
        // asks.
        PendingTeardowns.Register(id, BoundedTeardown.Steps.BackendDispose, later.Task, clock);

        PendingTeardowns.ThrowIfPending(id);

        // The record is still there; it is the refusal that ended.
        Assert.NotNull(PendingTeardowns.Find(id));

        first.SetResult();
        later.SetResult();
    }

    [Fact]
    public async Task CameraSessionOpen_RechecksAfterConfigure()
    {
        // CameraDevice.OpenSessionAsync has always rechecked after its Configure.
        // CameraSession.OpenAsync -- the path CameraSessionBuilder and so
        // CameraDeviceProxy take -- did not, and a refusal that can expire makes
        // that the only point at which a teardown abandoned during Configure would
        // be caught before a session is handed to the caller.
        var clock = new FakeTimeProvider();
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WINDOW\\CONFIGURE");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterOnOpenBackend? captured = null;

        var previous = CameraDevice.BackendFactory;
        CameraDevice.BackendFactory = _ =>
        {
            captured = new RegisterOnOpenBackend(device.Id, pending.Task, clock, onConfigure: true);
            return captured;
        };
        try
        {
            var config = new CameraConfiguration(CameraTestFormats.Vga, 30);
            var ex = await Assert.ThrowsAsync<CameraTeardownPendingException>(
                () => CameraSession.OpenAsync(device, config));
            Assert.Equal(device.Id, ex.DeviceId);
            Assert.True(captured!.InnerDisposed, "the backend opened during the race must be disposed");
        }
        finally
        {
            CameraDevice.BackendFactory = previous;
            pending.TrySetResult();
        }
    }

    [Fact]
    public async Task AnAdmittedOpen_WarnsWithTheStepsAndTheirAge()
    {
        // The counter says an admission happened; only the log says which steps
        // are parked and for how long, which is what tells an operator that the
        // open failure underneath it is contention rather than a camera that
        // cannot produce the format. That is the reading the #123 cascade got
        // wrong nineteen times, so it is pinned rather than assumed.
        var clock = new FakeTimeProvider();
        var device = TestHelpers.CreateDeviceInfo("TEST\\CAM\\WINDOW\\WARN");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTeardowns.Register(device.Id, BoundedTeardown.Steps.BackendDispose, gate.Task, clock);
        clock.Advance(PendingTeardowns.RefusalWindow + TimeSpan.FromSeconds(30));

        var logger = new CapturingLogger();
        try
        {
            using var scope = CameraTestScope.Install(_ => new InMemoryCameraBackend());
            await using var opened = await CameraDevice.OpenAsync(device, logger, clock, CancellationToken.None);

            var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains(device.Id, warning.Message);
            Assert.Contains(BoundedTeardown.Steps.BackendDispose, warning.Message);
            Assert.Contains("90.0s", warning.Message);
            Assert.Contains("60s refusal window", warning.Message);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public void RepeatedWedgesOnOneStep_DoNotGrowTheStepList_AndAreAllStillAwaited()
    {
        // A refusal that expires lets an admitted open wedge and register in turn,
        // so a driver that stays wedged while a proxy retries on a backoff puts
        // hundreds of registrations on one device. Every one used to append a step
        // name and nest another WhenAll: the operator's Warning and the exception
        // message would print the same three names over and over, and the field
        // report's 14 hours would be roughly 870 of them.
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\REPEAT";
        var gates = new List<TaskCompletionSource>();

        for (int i = 0; i < 50; i++)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Add(gate);
            PendingTeardowns.Register(id, BoundedTeardown.Steps.BackendDispose, gate.Task, clock);
        }

        var pending = PendingTeardowns.Find(id)!;

        // Which steps are parked is the diagnostic; how many times each was
        // retried is not, and it is already in the counter.
        Assert.Equal([BoundedTeardown.Steps.BackendDispose], pending.Steps);

        // Deduplicating the NAMES must not drop any of the parked calls. Release
        // the NEWEST one first: a composite that kept only the latest task would
        // clear here, and releasing oldest-first could not tell that apart from a
        // correct chain.
        Assert.False(pending.Completion.IsCompleted);
        var cleared = PendingTeardowns.WhenClearedAsync(id);

        gates[^1].SetResult();
        Assert.NotNull(PendingTeardowns.Find(id));
        Assert.False(cleared.IsCompleted);

        // Nor does any single older one lift it.
        for (int i = 0; i < gates.Count - 2; i++)
        {
            gates[i].SetResult();
            Assert.NotNull(PendingTeardowns.Find(id));
        }

        gates[^2].SetResult();
        Assert.True(cleared.Wait(TestHelpers.Patience));
        Assert.Null(PendingTeardowns.Find(id));
    }

    [Fact]
    public void TheRefusalMessage_DoesNotAdviseAReplug()
    {
        var clock = new FakeTimeProvider();
        var id = "TEST\\CAM\\WINDOW\\MESSAGE";
        PendingTeardowns.Register(id, BoundedTeardown.Steps.StopCapture, new TaskCompletionSource().Task, clock);

        var ex = Assert.Throws<CameraTeardownPendingException>(() => PendingTeardowns.ThrowIfPending(id));

        // The advice was to replug, which cannot lift this: the entry is keyed by
        // device id and the OS commonly hands the same id back, so the returning
        // device is refused on arrival (issue #221 item 1). Saying so is the point
        // -- a caller who reads "replug" does the one thing that does not work.
        Assert.DoesNotContain("replug the camera if", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Replugging may not clear it", ex.Message);
        Assert.Contains("lifts on its own", ex.Message);
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
    /// <summary>
    /// Records every entry's level and rendered message, so a test can assert on a
    /// diagnostic line rather than only on the counter beside it. Same shape as
    /// <c>Periphery.Bootloader.Efm8.Usb.Tests.CapturingLogger</c>.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly record struct Entry(LogLevel Level, string Message);

        public List<Entry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new Entry(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RegisterOnOpenBackend : ICameraBackend
    {
        private readonly InMemoryCameraBackend _inner = new();
        private readonly ICameraBackend _io;
        private readonly string _deviceId;
        private readonly Task _pending;
        private readonly TimeProvider _clock;
        private readonly bool _onConfigure;

        public RegisterOnOpenBackend(string deviceId, Task pending, TimeProvider clock, bool onConfigure = false)
        {
            _io = _inner;
            _deviceId = deviceId;
            _pending = pending;
            _clock = clock;
            _onConfigure = onConfigure;
        }

        public bool InnerDisposed => _inner.IsDisposed;

        public string NativeEndpointId => _io.NativeEndpointId;

        public async Task OpenAsync(CancellationToken ct)
        {
            await _io.OpenAsync(ct).ConfigureAwait(false);
            if (!_onConfigure)
                PendingTeardowns.Register(_deviceId, BoundedTeardown.Steps.StopCapture, _pending, _clock);
        }

        public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct) => _io.GetFormatsAsync(ct);
        public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct) => _io.GetControlsAsync(ct);
        public Task<CameraControlState?> GetControlAsync(CameraControlKind control, CancellationToken ct) => _io.GetControlAsync(control, ct);
        public Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct) => _io.SetControlAsync(control, value, ct);
        public Task ResetControlAsync(CameraControlKind control, CancellationToken ct) => _io.ResetControlAsync(control, ct);
        public async Task ConfigureAsync(CameraConfiguration configuration, CancellationToken ct)
        {
            await _io.ConfigureAsync(configuration, ct).ConfigureAwait(false);
            if (_onConfigure)
                PendingTeardowns.Register(_deviceId, BoundedTeardown.Steps.StopCapture, _pending, _clock);
        }
        public Task StartCaptureAsync(CancellationToken ct) => _io.StartCaptureAsync(ct);
        public Task<RawCameraFrame> ReadRawFrameAsync(CancellationToken ct) => _io.ReadRawFrameAsync(ct);
        public Task StopCaptureAsync() => _io.StopCaptureAsync();
        public ValueTask DisposeAsync() => _io.DisposeAsync();
    }
}
