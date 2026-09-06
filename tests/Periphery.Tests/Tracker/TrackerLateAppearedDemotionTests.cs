// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Tests;

/// <summary>
/// Characterises the core defect behind issue #177: <see cref="DeviceTrackerResolution"/>
/// holds two orthogonal latches but one shared <see cref="DeviceInfo"/> snapshot, and
/// derives activity from the snapshot rather than from the latch that already asserts it.
///
/// <para><c>Resolve()</c> gates <see cref="DeviceActivityStatus.Active"/> on
/// <c>_connectedLatch[profile]</c> AND <c>_devicesByProfile[profile][connId].IsActive</c>.
/// <c>ApplyAppeared</c> writes that same snapshot via <c>SetDevice</c>. So a presence edge
/// carrying <c>IsActive == false</c> overwrites the snapshot an activity assertion depends
/// on, and the tracker silently drops from Active to Present while the connected latch is
/// still held.</para>
///
/// <para><b>Why this is not Windows-specific.</b> These drive the platform-agnostic
/// <see cref="DeviceWatcher"/> through fake providers, so they exercise the same code path
/// on every OS. The Linux provider already emits exactly this shape — <c>HandleAdd</c>
/// raises <c>DeviceAppeared</c> unconditionally and only then raises <c>DeviceActivated</c>
/// if <c>device.IsActive</c> — and macOS mirrors it. Windows has been shielded only because
/// its <c>DeviceAppeared</c> never fired for a live arrival (the bug #177 diagnoses), so
/// fixing the Windows provider in isolation would expose this on a third platform rather
/// than introduce it.</para>
///
/// <para><b>Reachable without concurrent callbacks.</b> The interleaving modelled here is
/// the one <see cref="DeviceWatcher.StartAsync"/> already permits: the monitor provider goes
/// live before <c>SnapshotCurrentDevicesAsync</c> walks the tree, so the walk can build a
/// snapshot for a device that is present-but-not-yet-started, the provider can raise
/// <c>Activated</c> for it mid-walk, and the walk then raises <c>Appeared</c> with the now
/// stale <c>IsActive == false</c> payload.</para>
/// </summary>
public class TrackerLateAppearedDemotionTests
{
    private static DeviceInfo Device(bool isActive) => new()
    {
        Id = @"USB\VID_1234&PID_5678\TEST",
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
    };

    private static (DeviceWatcher Watcher, FakeDeviceMonitorProvider Monitor) Build()
    {
        var monitor = new FakeDeviceMonitorProvider();
        var watcher = new DeviceWatcher(new FakeDeviceProvider(), monitor);
        return (watcher, monitor);
    }

    /// <summary>
    /// The failing case. A tracker that has resolved to Active must not be demoted by a
    /// presence edge, because presence and activity are orthogonal (ADR-0004) and the
    /// connected latch is still held.
    /// </summary>
    [Fact]
    public async Task LateAppearedWithStalePayload_DoesNotDemoteAnActiveTracker()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;

        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        // The device starts: provider raises Activated.
        monitor.SimulateStatusChange(Device(isActive: true));
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);

        // A presence edge lands afterwards carrying a payload built before the device
        // started. SimulateConnect raises Appeared only, because IsActive is false.
        monitor.SimulateConnect(Device(isActive: false));

        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
    }

    /// <summary>
    /// The same sequence seen from the observable stream a consumer subscribes to. The
    /// device never stopped, so no deactivation transition should be published.
    /// </summary>
    [Fact]
    public async Task LateAppearedWithStalePayload_PublishesNoSpuriousDeactivation()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;

        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        monitor.SimulateStatusChange(Device(isActive: true));

        var transitions = new List<DeviceActivityStatus>();
        tracker.StateChanged += (_, _) => transitions.Add(tracker.ActivityStatus);

        monitor.SimulateConnect(Device(isActive: false));

        Assert.DoesNotContain(DeviceActivityStatus.Present, transitions);
    }

    /// <summary>
    /// Control. Ordered normally — presence first, then activity — the tracker resolves to
    /// Active and stays there. If this fails, the fixture is wrong rather than the library.
    /// </summary>
    [Fact]
    public async Task AppearedThenActivated_ResolvesToActive()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;

        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        monitor.SimulateConnect(Device(isActive: false));
        Assert.Equal(DeviceActivityStatus.Present, tracker.ActivityStatus);

        monitor.SimulateStatusChange(Device(isActive: true));
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
    }

    /// <summary>
    /// Control. A genuine deactivation still demotes — the fix must not make the tracker
    /// deaf to real transitions, only to presence edges carrying a stale activity flag.
    /// </summary>
    [Fact]
    public async Task GenuineDeactivation_StillDemotesToPresent()
    {
        var (watcher, monitor) = Build();
        await using var _ = watcher;

        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        monitor.SimulateConnect(Device(isActive: false));
        monitor.SimulateStatusChange(Device(isActive: true));
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);

        monitor.SimulateStatusChange(Device(isActive: false));

        Assert.Equal(DeviceActivityStatus.Present, tracker.ActivityStatus);
    }
}
