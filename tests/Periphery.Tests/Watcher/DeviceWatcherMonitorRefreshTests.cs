using System.Drawing;

namespace Periphery.Tests;

/// <summary>
/// End-to-end (fake-provider) coverage for the issue #149 monitor refresh path:
/// an unenriched hotplug appearance followed by a <c>DevicePropertyChanged</c>
/// carrying the enriched snapshot must flow watcher → tracker and re-stamp the
/// resolved device's DisplayConfig fields. Exercises the same plumbing the
/// Windows <c>WM_DISPLAYCHANGE</c> sink drives, without any OS APIs.
/// </summary>
public class DeviceWatcherMonitorRefreshTests
{
    private const string MonitorId = "DISPLAY\\DELA1234\\5&1a2b3c4d&0&UID256";

    private static DeviceInfo Monitor(string? monitorName = null, Size? resolution = null) => new()
    {
        Id = MonitorId,
        Name = "Generic PnP Monitor",
        Category = DeviceCategory.Monitor,
        IsActive = true,
        MonitorName = monitorName,
        DisplayResolution = resolution,
    };

    [Fact]
    public async Task DisplayChange_RestampsMonitorName_ThroughWatcherToTracker()
    {
        var provider = new FakeDeviceProvider();          // empty startup snapshot
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(provider, monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Monitor), "Monitor");

        await watcher.StartAsync();

        // Hotplug arrival is unenriched (the bug).
        var bare = Monitor(monitorName: null);
        monitor.SimulateConnect(bare);
        Assert.True(tracker.IsPresent);
        Assert.Null(tracker.Device!.MonitorName);

        // The WM_DISPLAYCHANGE sink re-enriches and emits the delta.
        var enriched = bare with { MonitorName = "DELL U2720Q", DisplayResolution = new Size(3840, 2160) };
        monitor.SimulatePropertyChange(bare, enriched);

        Assert.Equal("DELL U2720Q", tracker.Device!.MonitorName);
        Assert.Equal(new Size(3840, 2160), tracker.Device!.DisplayResolution);
    }

    [Fact]
    public async Task ModeChange_RestampsResolution_WithMonitorNameUnchanged()
    {
        // The previously-eventless case ADR-0066 closes: a resolution/rotation
        // change on an already-attached panel, where MonitorName does not change.
        var provider = new FakeDeviceProvider();
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(provider, monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Monitor), "Monitor");

        await watcher.StartAsync();

        var attached = Monitor(monitorName: "DELL U2720Q", resolution: new Size(1920, 1080));
        monitor.SimulateConnect(attached);
        Assert.Equal(new Size(1920, 1080), tracker.Device!.DisplayResolution);

        var afterModeChange = attached with { DisplayResolution = new Size(3840, 2160) };
        monitor.SimulatePropertyChange(attached, afterModeChange);

        Assert.Equal(new Size(3840, 2160), tracker.Device!.DisplayResolution);
        Assert.Equal("DELL U2720Q", tracker.Device!.MonitorName);
    }

    [Fact]
    public async Task AppearedAndActivated_BothCarryingEnrichment_KeepEnrichment()
    {
        // The arrival burst is Appeared + Activated. The tracker core is a full
        // replace on BOTH, so enrichment survives only if the provider supplies it
        // on both payloads — which is exactly what WindowsMonitorEnrichment
        // .MergeArrival guarantees on the real provider.
        var provider = new FakeDeviceProvider();
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(provider, monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Monitor), "Monitor");

        await watcher.StartAsync();

        // SimulateConnect raises Appeared then Activated with this same payload.
        var enriched = Monitor(monitorName: "DELL U2720Q", resolution: new Size(3840, 2160));
        monitor.SimulateConnect(enriched);

        Assert.True(tracker.IsActive);
        Assert.Equal("DELL U2720Q", tracker.Device!.MonitorName);
        Assert.Equal(new Size(3840, 2160), tracker.Device!.DisplayResolution);
    }

    [Fact]
    public async Task BareActivated_AfterEnrichedAppeared_ClobbersTracker()
    {
        // Documents WHY the provider-side merge is load-bearing: the tracker core
        // has no sticky/merge behaviour, so an un-merged Activated payload silently
        // drops enrichment. If this ever stops clobbering, the core gained merge
        // semantics and the provider-side guarantee should be re-examined.
        var provider = new FakeDeviceProvider();
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(provider, monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Monitor), "Monitor");

        await watcher.StartAsync();

        var enriched = Monitor(monitorName: "DELL U2720Q");
        monitor.SimulateConnect(enriched with { IsActive = false }); // Appeared only
        Assert.Equal("DELL U2720Q", tracker.Device!.MonitorName);

        monitor.SimulateStatusChange(Monitor(monitorName: null)); // bare Activated
        Assert.Null(tracker.Device!.MonitorName);
    }

    [Fact]
    public async Task PropertyChange_BeforeAppearance_IsDropped_AndNeverRecovers()
    {
        // Pins the precondition MonitorAnnouncementLedger enforces on the Windows
        // provider (ADR-0066 D2a): a refresh delta that beats the appearance is
        // dropped by the tracker — the device is not resolved yet — and nothing
        // re-emits it. That is why the provider skips (rather than writes back and
        // raises) a delta for a monitor whose publish is still in flight.
        var provider = new FakeDeviceProvider();
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(provider, monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Monitor), "Monitor");

        await watcher.StartAsync();

        var bare = Monitor(monitorName: null);
        var enriched = bare with { MonitorName = "DELL U2720Q" };

        monitor.SimulatePropertyChange(bare, enriched);
        Assert.False(tracker.IsPresent);

        // The appearance that follows carries only what the provider put in it, so
        // the enrichment delivered too early is simply lost.
        monitor.SimulateConnect(bare);
        Assert.True(tracker.IsPresent);
        Assert.Null(tracker.Device!.MonitorName);
    }

    [Fact]
    public async Task DisplayChange_FiresWatcherPropertyChanged_WithDisplayFieldsInDiff()
    {
        var provider = new FakeDeviceProvider();
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(provider, monitor);

        DevicePropertyChangedEventArgs? received = null;
        watcher.PropertyChanged += (_, e) => received = e;

        await watcher.StartAsync();

        var bare = Monitor(monitorName: null);
        monitor.SimulateConnect(bare);
        var enriched = bare with { MonitorName = "DELL U2720Q" };
        monitor.SimulatePropertyChange(bare, enriched);

        Assert.NotNull(received);
        Assert.Contains(nameof(DeviceInfo.MonitorName), received!.ChangedProperties);
    }
}
