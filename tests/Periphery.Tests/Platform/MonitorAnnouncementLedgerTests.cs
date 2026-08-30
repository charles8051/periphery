using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

/// <summary>
/// Unit tests for <see cref="MonitorAnnouncementLedger"/> — the ordering
/// precondition behind ADR-0066 Decision 2a, held as data rather than as a lock
/// around consumer callbacks (issue #153). Plain state, no OS calls, so these run
/// anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class MonitorAnnouncementLedgerTests
{
    private const string MonitorId = "DISPLAY\\DELA1234\\5&1a2b3c4d&0&UID256";

    [Fact]
    public void UnknownMonitor_IsNotRefreshEligible()
    {
        // The #149 hazard: a refresh that fires before anything has announced this
        // monitor must not raise a delta the tracker would silently drop.
        var ledger = new MonitorAnnouncementLedger();

        Assert.False(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void MonitorMidPublish_IsNotRefreshEligible_UntilItsEventsAreRaised()
    {
        var ledger = new MonitorAnnouncementLedger();

        ledger.BeginPublish(MonitorId);
        Assert.False(ledger.IsRefreshEligible(MonitorId));

        ledger.EndPublish(MonitorId);
        Assert.True(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void Republish_OfAnAnnouncedMonitor_SuspendsEligibility_ForItsDuration()
    {
        // A re-appearance re-raises DeviceAppeared with a freshly merged payload;
        // a refresh must not interleave a delta computed against the old snapshot.
        var ledger = new MonitorAnnouncementLedger();
        ledger.BeginPublish(MonitorId);
        ledger.EndPublish(MonitorId);

        ledger.BeginPublish(MonitorId);
        Assert.False(ledger.IsRefreshEligible(MonitorId));

        ledger.EndPublish(MonitorId);
        Assert.True(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void ConcurrentPublishes_OfTheSameMonitor_StayIneligible_UntilTheLastCompletes()
    {
        // One plug delivers BOTH the interface-arrival and the instance-started
        // notification, on different cfgmgr32 callback threads, so the same monitor
        // can be mid-publish twice at once. A flag would go eligible when the first
        // finished, while the second was still raising; the depth does not.
        var ledger = new MonitorAnnouncementLedger();

        ledger.BeginPublish(MonitorId);   // interface arrival
        ledger.BeginPublish(MonitorId);   // instance started

        ledger.EndPublish(MonitorId);
        Assert.False(ledger.IsRefreshEligible(MonitorId));

        ledger.EndPublish(MonitorId);
        Assert.True(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void SeededMonitor_IsRefreshEligible_WithoutAPublish()
    {
        // StartAsync's cache seed: consumers learn about these from the watcher's
        // startup snapshot, not from a provider event, so a later mode change must
        // still be free to emit a delta for them.
        var ledger = new MonitorAnnouncementLedger();

        ledger.MarkAnnounced(MonitorId);

        Assert.True(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void Forget_MakesAMonitorIneligible_Again()
    {
        var ledger = new MonitorAnnouncementLedger();
        ledger.MarkAnnounced(MonitorId);

        ledger.Forget(MonitorId);

        Assert.False(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void PublishCompletingAfterRemoval_DoesNotResurrectTheMonitor()
    {
        // A removal can land while an arrival publish is still raising its events.
        // The trailing EndPublish must not re-announce a monitor that is gone.
        var ledger = new MonitorAnnouncementLedger();
        ledger.BeginPublish(MonitorId);

        ledger.Forget(MonitorId);
        ledger.EndPublish(MonitorId);

        Assert.False(ledger.IsRefreshEligible(MonitorId));
    }

    [Fact]
    public void IdsAreMatched_CaseInsensitively()
    {
        // The snapshot/query path and the change-notification path can report the
        // same instance id in different case (see DeviceId); every DeviceId-keyed
        // map downstream — including the provider's own cache — is OrdinalIgnoreCase.
        var ledger = new MonitorAnnouncementLedger();

        ledger.BeginPublish(MonitorId);
        ledger.EndPublish(MonitorId.ToLowerInvariant());

        Assert.True(ledger.IsRefreshEligible(MonitorId.ToUpperInvariant()));
    }
}
