// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

namespace Periphery.Tests;

/// <summary>
/// Pins the three defects ADR-0087 closes at the <see cref="DeviceWatcher"/> fan-out
/// boundary. Each of these fails on the pre-ADR-0087 code, and each survives every
/// fix confined to <see cref="DeviceTrackerResolution"/> — which is the argument for
/// reconciling here rather than in the tracker.
///
/// <para><b>The mirror of #177.</b> #177 is a stale <i>inactive</i> payload demoting a
/// running device. Run the window backwards and a stale <i>active</i> payload latches a
/// tracker Active for a device the live stream already removed, permanently, because
/// <c>Disappeared</c> has been consumed by the time the walk publishes. A tracker-layer
/// guard is asymmetric — it protects the connected latch from presence writes and does
/// nothing about <c>ApplyConnected</c> latching after <c>ApplyDisappeared</c> found
/// nothing.</para>
///
/// <para><b>The replay cache.</b> <c>_deviceCache</c> feeds
/// <c>ReplayKnownDevicesTo</c>, so it decides what a <c>Reconfigure</c> sees. Written
/// only by the startup walk and by <c>PropertyChanged</c>, it misses every live arrival
/// and keeps every live removal.</para>
///
/// <para><b>Non-tracker consumers.</b> The watcher's own <c>Appeared</c> event is read
/// directly by readiness code. A fix inside the tracker never reaches it.</para>
/// </summary>
public class WatcherBoundaryReconciliationTests
{
    private static DeviceInfo Device(bool isActive) => new()
    {
        Id = @"USB\VID_1234&PID_5678\TEST",
        Name = "Test Device",
        Category = DeviceCategory.Usb,
        IsActive = isActive,
    };

    /// <summary>Query provider that runs a hook immediately before each yield.</summary>
    private sealed class HookedProvider(Action beforeYield, params DeviceInfo[] devices) : IDeviceProvider
    {
        public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
            DeviceFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var d in devices)
            {
                beforeYield();
                yield return d;
            }
        }
    }

    // ── The mirror of #177: a stale ACTIVE payload from the walk ───────

    /// <summary>
    /// The snapshot walk captured the device while it was running; the live stream
    /// then removed it; the walk publishes the stale payload afterwards. The tracker
    /// latches Active for a device that is gone, and nothing can ever clear it --
    /// Disappeared has already been consumed.
    /// </summary>
    [Fact]
    public async Task StaleActivePayloadFromWalk_LatchesAPhantomActiveDevice()
    {
        var monitor = new FakeDeviceMonitorProvider();
        var dev = Device(isActive: true);
        var fired = false;
        var query = new HookedProvider(() =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateDisconnect(dev); // Disappeared lands before the walk publishes
        }, dev);

        await using var watcher = new DeviceWatcher(query, monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
    }

    // ── An activity edge must not suppress the walk's announcement ─────

    /// <summary>
    /// A device already present at start can take an activity edge mid-walk - a driver
    /// restart, or a Bluetooth peripheral coming into range - without ever leaving the
    /// tree. That edge says nothing about presence, so it must not suppress the snapshot,
    /// which is the only thing establishing presence for a device that never arrived.
    ///
    /// <para>Suppressing it is also unnecessary: the reconciliation at the fan-out boundary
    /// already stops the walk's stale payload demoting a device the live stream activated.
    /// Presence supersession and activity reconciliation are complementary.</para>
    /// </summary>
    [Fact]
    public async Task ActivityEdgeDuringTheWalk_DoesNotSuppressTheSnapshotAppearance()
    {
        var monitor = new FakeDeviceMonitorProvider();
        var dev = Device(isActive: true);
        var fired = false;
        var query = new HookedProvider(() =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateStatusChange(dev);   // live Activated, no arrival edge
        }, dev);

        await using var watcher = new DeviceWatcher(query, monitor);
        var appeared = new List<DeviceId>();
        watcher.Appeared += (_, e) => appeared.Add(e.Device.Id);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        Assert.Contains(dev.Id, appeared);
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
    }

    /// <summary>
    /// The walk raises <c>Appeared</c> directly rather than through
    /// <c>OnProviderAppeared</c>, so its payload has to be reconciled on that path too. A
    /// walk snapshot captured before the live stream activated the device carries
    /// <c>IsActive == false</c>, and publishing it unreconciled demotes a running device -
    /// the #177 defect, reached through the startup walk instead of a live edge.
    /// </summary>
    [Fact]
    public async Task StaleInactiveWalkPayload_IsReconciledBeforePublication()
    {
        var monitor = new FakeDeviceMonitorProvider();
        var stale = Device(isActive: false);   // captured before the device started
        var fired = false;
        var query = new HookedProvider(() =>
        {
            if (fired) return;
            fired = true;
            monitor.SimulateStatusChange(Device(isActive: true));   // live activation first
        }, stale);

        await using var watcher = new DeviceWatcher(query, monitor);
        DeviceInfo? announced = null;
        watcher.Appeared += (_, e) => announced = e.Device;
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        Assert.NotNull(announced);
        Assert.True(announced!.IsActive);
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
    }

    // ── Reconfigure must not lose a device that arrived live ───────────

    /// <summary>
    /// _deviceCache is written only by the startup walk and by PropertyChanged.
    /// OnProviderAppeared/OnProviderActivated never write it, so a device that
    /// arrived live is invisible to ReplayKnownDevicesTo and a reconfigure erases it.
    /// </summary>
    [Fact]
    public async Task ReconfigureAfterALiveArrival_LosesTheDevice()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(FakeDeviceProvider.Empty(), monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();

        monitor.SimulateConnect(Device(isActive: true));
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);

        tracker.Reconfigure(f => f.OfCategory(DeviceCategory.Usb)); // same filter

        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);
    }

    // ── Reconfigure must not resurrect a device that was removed ───────

    /// <summary>
    /// _deviceCache is never pruned on Disappeared either, so the replay re-latches
    /// a device the tracker had already correctly dropped.
    /// </summary>
    [Fact]
    public async Task ReconfigureAfterARemoval_ResurrectsTheDevice()
    {
        var monitor = new FakeDeviceMonitorProvider();
        var dev = Device(isActive: true);
        await using var watcher = new DeviceWatcher(new FakeDeviceProvider(dev), monitor);
        var tracker = watcher.AddTracker(f => f.OfCategory(DeviceCategory.Usb), name: "Device");
        await watcher.StartAsync();
        Assert.Equal(DeviceActivityStatus.Active, tracker.ActivityStatus);

        monitor.SimulateDisconnect(dev);
        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);

        tracker.Reconfigure(f => f.OfCategory(DeviceCategory.Usb));

        Assert.Equal(DeviceActivityStatus.Absent, tracker.ActivityStatus);
    }

    // ── The raw Appeared payload a non-tracker consumer sees ───────────

    /// <summary>
    /// A fix confined to DeviceTrackerResolution never reaches this. Consumers that
    /// read IsActive off the watcher's own Appeared event get the stale false and, in
    /// the readiness case, treat a not-yet-started device as ready.
    /// </summary>
    [Fact]
    public async Task RawAppearedEvent_StillCarriesTheStaleInactiveFlag()
    {
        var monitor = new FakeDeviceMonitorProvider();
        await using var watcher = new DeviceWatcher(FakeDeviceProvider.Empty(), monitor);
        await watcher.StartAsync();

        monitor.SimulateStatusChange(Device(isActive: true));

        DeviceInfo? seen = null;
        watcher.Appeared += (_, e) => seen = e.Device;
        monitor.SimulateConnect(Device(isActive: false));

        Assert.NotNull(seen);
        Assert.True(seen!.IsActive);
    }
}
