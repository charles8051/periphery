// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Tests;

/// <summary>
/// The window between the top of a presence handler and its tracker fan-out. The public
/// <see cref="DeviceWatcher.Appeared"/> raise runs consumer code in between, and a
/// concurrent edge for the same id can land while it runs. On Windows the two halves of
/// one hot-plug arrive as separate cfgmgr32 notifications with nothing serialising them,
/// so the window is a race per arrival; during startup the walk and the live stream
/// overlap on every platform.
///
/// <para>These model the arrival deterministically by raising the second edge from inside
/// the first one's public handler, on the same thread. Issue #201 records the class; these
/// pin the two consequences that close without an ordering guarantee: a tracker must not
/// be demoted by a payload that was already stale when it was fanned out, and a tracker
/// must not be told about a device the watcher has already seen leave.</para>
///
/// <para>The last test is issue #202: an <c>Activated</c> edge whose payload says
/// <c>IsActive == false</c> is not evidence of activity. The pure core already refuses to
/// resolve it as Active (the fail-safe ADR-0087 kept); what must not happen is that the
/// watcher records it as an activation anyway and then deduplicates the real one.</para>
/// </summary>
public class WatcherFanOutWindowTests
{
    private const string Id = @"USB\VID_1234&PID_5678\WINDOW";

    private static DeviceInfo Device(bool isActive) => new()
    {
        Id = Id,
        Name = "Window Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
    };

    private static (DeviceWatcher Watcher, FakeDeviceMonitorProvider Monitor) Build(params DeviceInfo[] snapshot)
    {
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(new FakeDeviceProvider(snapshot), monitor);
        return (watcher, monitor);
    }

    // ── Live path ──────────────────────────────────────────────────────

    [Fact]
    public async Task LivePath_ActivatedLandsDuringTheAppearedRaise_TrackerEndsActive()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        var deactivations = 0;
        tracker.Deactivated += (_, _) => deactivations++;
        await watcher.StartAsync();

        // The device starts while the presence handler is still running consumer code.
        var fired = false;
        watcher.Appeared += (_, _) =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateStatusChange(Device(isActive: true));
        };

        monitor.SimulateConnect(Device(isActive: false)); // Appeared only

        Assert.True(fired);
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
        Assert.Equal(0, deactivations);
    }

    [Fact]
    public async Task LivePath_DisappearedLandsDuringTheAppearedRaise_TrackerEndsAbsent()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        // The device leaves while the presence handler is still running consumer code.
        // Its Disappeared fan-out runs first and finds nothing to remove; the Appeared
        // fan-out that follows must not announce a device the watcher has seen leave.
        var fired = false;
        watcher.Appeared += (_, e) =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateDisconnect(e.Device);
        };

        monitor.SimulateConnect(Device(isActive: false)); // Appeared only

        Assert.True(fired);
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        Assert.Null(tracker.Device);
    }

    // ── Startup walk ───────────────────────────────────────────────────

    [Fact]
    public async Task Walk_ActivatedLandsDuringTheAppearedRaise_TrackerEndsActive()
    {
        // The walk captured the device before it started; the live stream reports the
        // start while the walk's public Appeared raise is running.
        var (watcher, monitor) = Build(Device(isActive: false));
        await using var _ = watcher;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        var deactivations = 0;
        tracker.Deactivated += (_, _) => deactivations++;

        var fired = false;
        watcher.Appeared += (_, _) =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateStatusChange(Device(isActive: true));
        };

        await watcher.StartAsync();

        Assert.True(fired);
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
        Assert.Equal(0, deactivations);
    }

    [Fact]
    public async Task Walk_DisappearedLandsDuringTheAppearedRaise_TrackerEndsAbsent()
    {
        // The walk captured an active device; it is unplugged while the walk's public
        // Appeared raise is running. The walk must not go on to announce Activated for it,
        // publicly or to trackers, and must not leave its id in the activity set.
        var (watcher, monitor) = Build(Device(isActive: true));
        await using var _ = watcher;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        var activated = new List<string>();
        watcher.Activated += (_, e) => activated.Add(e.Device.Id);

        var fired = false;
        watcher.Appeared += (_, e) =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateDisconnect(e.Device);
        };

        await watcher.StartAsync();

        Assert.True(fired);
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
        Assert.Empty(activated);

        // If the id had been left in the activity set, this genuine re-arrival would be
        // deduplicated and the tracker would stay Present.
        monitor.SimulateConnect(Device(isActive: true));
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
    }

    // ── Issue #202 ─────────────────────────────────────────────────────

    [Fact]
    public async Task ActivatedWithAnInactivePayload_IsNotAnActivation_AndDoesNotWedgeTheRealOne()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        var activated = new List<bool>();
        watcher.Activated += (_, e) => activated.Add(e.Device.IsActive);
        await watcher.StartAsync();

        monitor.SimulateConnect(Device(isActive: false));       // Appeared only
        monitor.SimulateActivatedEdge(Device(isActive: false)); // an ungated raise, or a failed status read

        // Fail-safe, as ADR-0087 decided: a payload that says inactive does not resolve
        // Active, and the watcher does not announce an activation it has no evidence for.
        Assert.Equal(DeviceActivityStatus.Present, tracker.ActivityStatus);
        Assert.Empty(activated);

        // But fail-safe is not permanent: the real activation still goes through.
        monitor.SimulateStatusChange(Device(isActive: true));

        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
        Assert.Equal(new[] { true }, activated);
    }

    [Fact]
    public async Task ActivatedWithAnInactivePayload_LeavesAnActiveDeviceAndItsCacheEntryAlone()
    {
        // The edge is treated as no edge: it must not write the replay cache either, or a
        // later Reconfigure would replay the device as Present.
        var (watcher, monitor) = Build();
        await using var _ = watcher;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        monitor.SimulateConnect(Device(isActive: true)); // Appeared + Activated
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);

        monitor.SimulateActivatedEdge(Device(isActive: false));

        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
        Assert.True(Assert.Single(watcher.KnownDevices).IsActive);
    }
}
