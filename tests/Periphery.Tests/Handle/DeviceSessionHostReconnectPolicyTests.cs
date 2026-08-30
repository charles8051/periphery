using System.Collections.Concurrent;

namespace Periphery.Tests;

/// <summary>
/// Covers the reconnect-policy seam forwarded from <see cref="DeviceSessionHost{TSession}"/>
/// down to its inner device handle: a give-up policy surfaces the terminal
/// <see cref="SessionGaveUp{TSession}"/> status and <see cref="ConnectionState.GaveUp"/>;
/// a retrying policy stays transient; the default (no policy) path still opens.
/// </summary>
public class DeviceSessionHostReconnectPolicyTests
{
    /// <summary>
    /// Stub policy with a scripted give-up point. Returns <paramref name="delay"/>
    /// until <c>Attempt &gt; GiveUpAfter</c>, then returns <see langword="null"/>
    /// (give up). Records the attempts it saw for assertions.
    /// </summary>
    private sealed class StubReconnectPolicy : IRecoveryPolicy
    {
        private readonly TimeSpan _delay;
        private readonly int _giveUpAfter;

        public StubReconnectPolicy(int giveUpAfter, TimeSpan? delay = null)
        {
            _giveUpAfter = giveUpAfter;
            _delay = delay ?? TimeSpan.FromMilliseconds(5);
        }

        public ConcurrentQueue<int> ObservedAttempts { get; } = new();

        public RecoveryDirective Decide(RecoveryContext context)
        {
            ObservedAttempts.Enqueue(context.Attempt);
            return context.Attempt > _giveUpAfter
                ? new RecoveryDirective.GiveUp()
                : new RecoveryDirective.Retry(_delay);
        }
    }

    [Fact]
    public async Task GiveUpPolicy_OpenAlwaysFails_SurfacesSessionGaveUpAndConnectionStateGaveUp()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();
        // Give up immediately: NextDelayAsync returns null on the first (Attempt == 1) call.
        var policy = new StubReconnectPolicy(giveUpAfter: 0);

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) =>
                throw new InvalidOperationException("device present but unopenable"),
            recoveryPolicy: policy);

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        var gaveUp = await SessionHostTestHelpers.WaitForStatusAsync<
            SessionHostTestHelpers.FakeSession,
            SessionGaveUp<SessionHostTestHelpers.FakeSession>>(host, TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.GaveUp, host.ConnectionState);
        Assert.NotNull(gaveUp.LastError);
        Assert.IsType<InvalidOperationException>(gaveUp.LastError);
        Assert.Equal(1, gaveUp.Attempt);
        Assert.False(host.HasSession);
        Assert.Contains("Gave up", host.StatusDescription);
    }

    [Fact]
    public async Task RetryingPolicy_OpenFailsThenSucceeds_StaysTransientThenActivates()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();
        var attempts = 0;
        // Never gives up; tiny delay so the retry loop turns over quickly.
        var policy = new StubReconnectPolicy(giveUpAfter: 100, delay: TimeSpan.FromMilliseconds(5));

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                    throw new InvalidOperationException($"init failed {attempts}");

                return Task.FromResult(new SessionHostTestHelpers.FakeSession { Id = attempts });
            },
            recoveryPolicy: policy);

        // Latch the transient unavailable transition on a signal wired up BEFORE the
        // device is connected, rather than reading host.Status after the fact. The
        // retry loop runs on the thread pool with real (if tiny) Task.Delay backoffs,
        // so a post-connect status read races it: under load the host can already have
        // recovered to SessionActive by the time we look, the intermediate
        // SessionUnavailable is missed, and a wait for a *future* one would hang out to
        // its timeout (the ~5s CI flake). Subscribing first makes the observation
        // timing-independent — SessionUnavailable always fires (attempt 1 always fails),
        // and the handler is attached before it can.
        var sawUnavailable = new TaskCompletionSource<SessionUnavailable<SessionHostTestHelpers.FakeSession>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.StatusChanged += (_, status) =>
        {
            if (status is SessionUnavailable<SessionHostTestHelpers.FakeSession> unavailable)
                sawUnavailable.TrySetResult(unavailable);
        };

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        // It must pass through the transient (non-terminal) unavailable state first...
        var unavailable = await sawUnavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(unavailable.LastError);

        // ...and then recover to an active session (a retrying policy is not terminal).
        var session = await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, session.Id);
        Assert.True(host.HasSession);
        Assert.Equal(ConnectionState.Open, host.ConnectionState);
        Assert.IsType<SessionActive<SessionHostTestHelpers.FakeSession>>(host.Status);
    }

    [Fact]
    public async Task DefaultPolicy_NoPolicyPassed_OpensSessionAndReportsConnectionStateOpen()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        // No reconnectPolicy argument: must behave exactly as before (open succeeds).
        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(
                new SessionHostTestHelpers.FakeSession { Id = 5 }));

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        var session = await host.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, session.Id);
        Assert.Equal(ConnectionState.Open, host.ConnectionState);
        Assert.IsType<SessionActive<SessionHostTestHelpers.FakeSession>>(host.Status);
    }

    [Fact]
    public async Task ConnectionState_BeforeAnyDevice_IsDisconnected()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => Task.FromResult(new SessionHostTestHelpers.FakeSession()));

        Assert.Equal(ConnectionState.Disconnected, host.ConnectionState);
    }

    [Fact]
    public async Task GiveUpPolicy_RaisesPropertyChangedForConnectionState()
    {
        var (tracker, _) = SessionHostTestHelpers.CreateTestInfra();
        var policy = new StubReconnectPolicy(giveUpAfter: 0);

        await using var host = DeviceSessionHost<SessionHostTestHelpers.FakeSession>.Create(
            tracker,
            createSession: (_, _) => throw new InvalidOperationException("nope"),
            recoveryPolicy: policy);

        var connStateNotified = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(
                    DeviceSessionHost<SessionHostTestHelpers.FakeSession>.ConnectionState))
                connStateNotified.TrySetResult();
        };

        SessionHostTestHelpers.SimulateConnect(tracker, SessionHostTestHelpers.MakeDevice());

        await connStateNotified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.GaveUp, host.ConnectionState);
    }
}
