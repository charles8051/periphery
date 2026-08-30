namespace Periphery.Monitor.Tests;

/// <summary>
/// Pins the empty-layout classification (issue #207). The whole point of
/// splitting this out as a pure function is that the session-0 case — which
/// otherwise only ever occurs inside a Windows service or over SSH — is
/// testable from an ordinary interactive test run.
/// </summary>
public class MonitorSessionVisibilityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AnyEntries_IsAvailable_RegardlessOfSession(int count)
    {
        // A non-empty read is self-evidently visible; the session never overrides it.
        Assert.Equal(
            MonitorLayoutAvailability.Available,
            MonitorSessionVisibility.Classify(count, sessionId: 0));
        Assert.Equal(
            MonitorLayoutAvailability.Available,
            MonitorSessionVisibility.Classify(count, sessionId: 1));
    }

    [Fact]
    public void Empty_InSessionZero_IsNotVisibleFromThisSession()
    {
        // The measured case: an SSH shell / Windows service reports zero monitors
        // on a box whose displays are attached and working.
        Assert.Equal(
            MonitorLayoutAvailability.NotVisibleFromThisSession,
            MonitorSessionVisibility.Classify(0, sessionId: 0));
    }

    [Theory]
    [InlineData(1u)]          // console
    [InlineData(2u)]          // a second/RDP session
    [InlineData(uint.MaxValue)] // session id unavailable — must not fabricate blindness
    public void Empty_OutsideSessionZero_IsNoActiveDisplays(uint sessionId)
    {
        // Deliberately NOT claiming blindness for any non-console session: an RDP
        // session has its own display configuration and legitimately sees its own
        // monitors, so "not the console" does not imply "cannot see".
        Assert.Equal(
            MonitorLayoutAvailability.NoActiveDisplays,
            MonitorSessionVisibility.Classify(0, sessionId));
    }

    [Fact]
    public void SessionZeroClaim_IsLimitedToTheEmptyCase()
    {
        // Guards the ordering inside Classify: a service that somehow DOES read
        // entries must report them as Available, not be overridden by its session.
        Assert.Equal(
            MonitorLayoutAvailability.Available,
            MonitorSessionVisibility.Classify(2, MonitorSessionVisibility.ServicesSessionId));
    }

    [Fact]
    public void NotMeasured_IsTheZeroValue_SoADefaultAssertsTheLeast()
    {
        // issue #210. A default-constructed or zero-initialized value must not
        // claim the topology was read -- Available used to sit at 0, which made
        // default(MonitorLayoutAvailability) the strongest positive claim in the
        // enum.
        Assert.Equal(MonitorLayoutAvailability.NotMeasured, default(MonitorLayoutAvailability));
    }

    [Fact]
    public void Classify_NeverReturnsNotMeasured()
    {
        // Classify only ever runs AFTER a read, so it cannot legitimately produce
        // "no read was performed". NotMeasured belongs to callers that skipped the
        // query entirely (a non-Windows fallback), not to this function.
        foreach (uint session in new uint[] { 0, 1, 2, uint.MaxValue })
            foreach (int count in new[] { 0, 1, 4 })
                Assert.NotEqual(
                    MonitorLayoutAvailability.NotMeasured,
                    MonitorSessionVisibility.Classify(count, session));
    }
}
