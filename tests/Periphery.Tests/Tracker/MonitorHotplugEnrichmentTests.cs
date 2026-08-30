using System.Collections.Generic;
using System.Drawing;

namespace Periphery.Tests;

/// <summary>
/// Tracker-side coverage for issue #149. The DisplayConfig re-stamp travels as a
/// <c>DevicePropertyChanged</c> carrying an enriched snapshot, which the tracker
/// applies via <see cref="DeviceTrackerResolution.ApplyPropertyChanged"/>. This is
/// the pure-core side of the Windows <c>WM_DISPLAYCHANGE</c> refresh hook; the
/// hidden-window sink and the arrival-time merge live in the Windows provider
/// shell (see <c>WindowsMonitorEnrichmentTests</c>), keeping this core
/// category-blind.
/// </summary>
public class MonitorHotplugEnrichmentTests
{
    private const string MonitorId = "DISPLAY\\DELA1234\\5&1a2b3c4d&0&UID256";

    private static DeviceInfo Monitor(
        string? monitorName = null,
        Size? resolution = null,
        Rectangle? bounds = null,
        bool isActive = true) => new()
    {
        Id = MonitorId,
        Name = "Generic PnP Monitor",
        Category = DeviceCategory.Monitor,
        IsActive = isActive,
        MonitorName = monitorName,
        DisplayResolution = resolution,
        DisplayBounds = bounds,
    };

    private static DeviceFilter MonitorFilter()
    {
        var f = new DeviceFilter();
        f.OfCategory(DeviceCategory.Monitor);
        return f;
    }

    [Fact]
    public void PropertyChange_AfterUnenrichedAppearance_RestampsDisplayConfigFields()
    {
        // A monitor arrives unenriched (bug), then the refresh hook delivers the
        // enriched snapshot as a property change → tracker re-stamps it.
        var tracker = new DeviceTracker(MonitorFilter());
        var bare = Monitor(monitorName: null);
        tracker.OnDeviceAppeared(bare);

        Assert.True(tracker.IsPresent);
        Assert.Null(tracker.Device!.MonitorName);

        var enriched = bare with
        {
            MonitorName = "DELL U2720Q",
            DisplayResolution = new Size(3840, 2160),
            DisplayBounds = new Rectangle(0, 0, 3840, 2160),
        };
        tracker.OnDevicePropertyChanged(bare, enriched,
            new HashSet<string> { nameof(DeviceInfo.MonitorName), nameof(DeviceInfo.DisplayResolution) });

        Assert.Equal("DELL U2720Q", tracker.Device!.MonitorName);
        Assert.Equal(new Size(3840, 2160), tracker.Device!.DisplayResolution);
        Assert.Equal(new Rectangle(0, 0, 3840, 2160), tracker.Device!.DisplayBounds);
    }

    [Fact]
    public void BareAppearance_LeavesDisplayFieldsNull_UntilRefreshHook()
    {
        // The pure core does not enrich on appearance — that is the provider's job
        // (arrival-time merge + the WM_DISPLAYCHANGE refresh). This pins that the
        // core stays category-blind: a bare appeared payload resolves as-is.
        var res = DeviceTrackerResolution.Create([new DeviceProfile(MonitorFilter(), "Monitor")]);

        res = res.ApplyAppeared(Monitor(monitorName: null));

        Assert.Null(res.Resolve().Device!.MonitorName);
    }

    [Fact]
    public void ApplyPropertyChanged_FullyReplaces_CanClearMonitorName()
    {
        // The refresh path is a full replace, so a device legitimately clearing a
        // field still clears it — the core has no sticky/merge behaviour.
        var res = DeviceTrackerResolution.Create([new DeviceProfile(MonitorFilter(), "Monitor")])
            .ApplyAppeared(Monitor(monitorName: "DELL U2720Q"));

        res = res.ApplyPropertyChanged(Monitor(monitorName: null));

        Assert.Null(res.Resolve().Device!.MonitorName);
    }
}
