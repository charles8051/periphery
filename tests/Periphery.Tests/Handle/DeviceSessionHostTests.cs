namespace Periphery.Tests;

public class DeviceSessionHostTests
{
    [Fact]
    public async Task Create_DeviceActivation_PublishesSessionAndStatusActive()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(
                new SessionHostTestHelpers.FakeSession { Id = 1 }));

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        var session = await host.WaitForSessionAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(host.HasSession);
        Assert.Same(session, host.GetRequiredSession());

        var active = Assert.IsType<SessionActive<SessionHostTestHelpers.FakeSession>>(host.Status);
        Assert.Equal(1, active.Session.Id);
        Assert.NotNull(host.DeviceInfo);
    }

    [Fact]
    public async Task Create_CreateSessionFailure_SetsSessionUnavailable_ThenRecovers()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();
        var attempts = 0;
        var recovered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                    throw new InvalidOperationException($"init failed {attempts}");

                recovered.TrySetResult();
                return Task.FromResult(new SessionHostTestHelpers.FakeSession { Id = attempts });
            });

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        var unavailable =
            await SessionHostTestHelpers.WaitForStatusAsync<
                SessionHostTestHelpers.FakeSession,
                SessionUnavailable<SessionHostTestHelpers.FakeSession>>(host, TimeSpan.FromSeconds(5));

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var active = await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, unavailable.Attempt);
        Assert.NotNull(unavailable.LastError);
        Assert.True(host.HasSession);
        Assert.Equal(3, active.Id);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Create_DeviceDisconnect_SetsDeviceAbsent_AndRunsSessionEnded()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();
        var device = SessionHostTestHelpers.MakeDevice();
        var cleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(
                new SessionHostTestHelpers.FakeSession { Id = 7 }),
            onSessionEnded: _ =>
            {
                cleanup.TrySetResult();
                return Task.CompletedTask;
            });

        SessionHostTestHelpers.SimulateConnect(tracker, device);
        await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        SessionHostTestHelpers.SimulateDisconnect(tracker, device);

        await SessionHostTestHelpers.WaitForStatusAsync<
            SessionHostTestHelpers.FakeSession,
            DeviceAbsent<SessionHostTestHelpers.FakeSession>>(host, TimeSpan.FromSeconds(5));
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(host.HasSession);
        Assert.Null(host.CurrentSession);
    }

    [Fact]
    public async Task WaitForSessionAsync_BeforeActivation_CompletesWhenDeviceConnects()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(
                new SessionHostTestHelpers.FakeSession { Id = 12 }));

        var waitTask = host.WaitForSessionAsync();

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        var session = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(12, session.Id);
    }

    [Fact]
    public async Task Create_WhileSessionActiveException_ClosesSession()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();
        var device = SessionHostTestHelpers.MakeDevice();
        var workerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(
                new SessionHostTestHelpers.FakeSession { Id = 99 }),
            whileSessionActive: async (_, ct) =>
            {
                workerStarted.TrySetResult();
                await Task.Delay(50, ct); // let session become visible
                throw new InvalidOperationException("worker crash");
            });

        SessionHostTestHelpers.SimulateConnect(tracker, device);
        await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await SessionHostTestHelpers.WaitForStatusAsync<
            SessionHostTestHelpers.FakeSession,
            DeviceAbsent<SessionHostTestHelpers.FakeSession>>(host, TimeSpan.FromSeconds(5));

        Assert.False(host.HasSession);
    }

    // ── ForDeviceAsync (sugar) ─────────────────────────────────────────

    [Fact]
    public async Task ForDeviceAsync_NullDevice_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DeviceSessionHost<SessionHostTestHelpers.FakeSession>.ForDeviceAsync(
                device: null!,
                createSession: (_, _) => Task.FromResult(new SessionHostTestHelpers.FakeSession())));
    }

    // ── StatusDescription (UI-friendly status text) ────────────────────

    [Fact]
    public async Task StatusDescription_DeviceAbsent_HasFriendlyText()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(new SessionHostTestHelpers.FakeSession()));

        Assert.Equal("Waiting for device.", host.StatusDescription);
    }

    [Fact]
    public async Task StatusDescription_SessionActive_IncludesDeviceName()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(
                new SessionHostTestHelpers.FakeSession { Id = 1 }));

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());
        await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var description = host.StatusDescription;
        Assert.Contains("Live", description);
        Assert.Contains("Test Device", description);
    }

    [Fact]
    public async Task StatusDescription_SessionUnavailable_IncludesAttemptAndError()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) =>
                throw new InvalidOperationException("synthetic init failure"));

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        await SessionHostTestHelpers.WaitForStatusAsync<
            SessionHostTestHelpers.FakeSession,
            SessionUnavailable<SessionHostTestHelpers.FakeSession>>(host, TimeSpan.FromSeconds(5));

        var description = host.StatusDescription;
        Assert.Contains("Unavailable", description);
        Assert.Contains("attempt", description);
        Assert.Contains("synthetic init failure", description);
    }

    [Fact]
    public async Task StatusDescription_RaisesPropertyChangedOnTransition()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(new SessionHostTestHelpers.FakeSession()));

        var notified = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceSessionHost<SessionHostTestHelpers.FakeSession>.StatusDescription))
                notified.TrySetResult();
        };

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());
        await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
