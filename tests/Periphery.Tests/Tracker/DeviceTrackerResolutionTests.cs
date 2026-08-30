namespace Periphery.Tests;

/// <summary>
/// Direct unit tests for the pure transition core
/// <see cref="DeviceTrackerResolution"/> — the immutable latch + resolution
/// value extracted from <see cref="DeviceTracker"/> (review finding 1.1,
/// ADR-0006 §3/§9, ADR-0052 functional core).
///
/// <para>These drive the core as values: build a state from a profile list,
/// fold a sequence of <c>Apply*</c> transitions over it, and assert the
/// resolved <see cref="DeviceTrackerState"/>. No watcher, no lock, no events,
/// no async — the core is total and side-effect-free, so this is the real test
/// surface for the latch rules. The shell-level behaviour
/// (<c>StateChanged</c>/<c>IObserver</c> emission, the <c>Unknown</c> sentinel)
/// is covered by <see cref="DeviceTrackerTests"/>.</para>
/// </summary>
public class DeviceTrackerResolutionTests
{
    private static DeviceInfo MakeDevice(
        string id = "USB\\VID_046D&PID_C52B\\1",
        string? name = "Logitech Mouse",
        DeviceCategory category = DeviceCategory.Usb,
        bool isActive = true) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        IsActive = isActive,
        VendorId = new HardwareId(0x046D),
        ProductId = new HardwareId(0xC52B),
    };

    private static DeviceProfile Profile(
        Action<DeviceFilter>? configure = null, string? name = null)
    {
        var filter = new DeviceFilter();
        // Default: match everything in the test's universe by category Usb OR
        // Bluetooth OR Network OR ... is awkward; instead a no-criteria filter
        // is illegal, so the catch-all test profile matches by a broad category.
        (configure ?? (f => f.OfCategory(DeviceCategory.Usb)))(filter);
        return new DeviceProfile(filter, name);
    }

    /// <summary>A single catch-all USB profile — the common single-profile case.</summary>
    private static DeviceTrackerResolution SingleUsb() =>
        DeviceTrackerResolution.Create([Profile(f => f.OfCategory(DeviceCategory.Usb))]);

    // ── Empty / default ────────────────────────────────────────────────

    [Fact]
    public void Create_EmptyState_ResolvesToAbsent()
    {
        var state = SingleUsb();

        var resolved = state.Resolve();

        Assert.Equal(DeviceActivityStatus.Absent, resolved.ActivityStatus);
        Assert.Null(resolved.Device);
        Assert.Null(resolved.ActiveProfile);
        Assert.False(resolved.IsPresent);
        Assert.False(resolved.IsActive);
    }

    // ── Appeared (present dimension) ───────────────────────────────────

    [Fact]
    public void ApplyAppeared_MatchingDevice_ResolvesPresent()
    {
        var resolved = SingleUsb().ApplyAppeared(MakeDevice()).Resolve();

        Assert.Equal(DeviceActivityStatus.Present, resolved.ActivityStatus);
        Assert.NotNull(resolved.Device);
        Assert.False(resolved.IsActive);
        Assert.True(resolved.IsPresent);
    }

    [Fact]
    public void ApplyAppeared_NonMatchingDevice_StaysAbsent_ReturnsSameInstance()
    {
        var state = SingleUsb();
        var bt = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth);

        var next = state.ApplyAppeared(bt);

        Assert.Same(state, next); // no profile matched → no new instance
        Assert.Equal(DeviceActivityStatus.Absent, next.Resolve().ActivityStatus);
    }

    [Fact]
    public void ApplyAppeared_SecondDevice_RejectedByLatch()
    {
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        var resolved = SingleUsb()
            .ApplyAppeared(first)
            .ApplyAppeared(second)
            .Resolve();

        Assert.Equal(first.Id, resolved.Device!.Id); // latch holds first
    }

    // ── Connected (connected dimension) ────────────────────────────────

    [Fact]
    public void ApplyConnected_MatchingActiveDevice_ResolvesActive()
    {
        var resolved = SingleUsb().ApplyConnected(MakeDevice()).Resolve();

        Assert.Equal(DeviceActivityStatus.Active, resolved.ActivityStatus);
        Assert.NotNull(resolved.Device);
        Assert.True(resolved.IsActive);
        Assert.True(resolved.IsPresent); // Active implies a non-null Device
    }

    [Fact]
    public void ApplyConnected_SecondDevice_RejectedByLatch()
    {
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        var resolved = SingleUsb()
            .ApplyConnected(first)
            .ApplyConnected(second)
            .Resolve();

        Assert.Equal(first.Id, resolved.Device!.Id);
        Assert.Equal(DeviceActivityStatus.Active, resolved.ActivityStatus);
    }

    [Fact]
    public void ApplyConnected_InactiveSnapshot_DoesNotResolveActive()
    {
        // Connected latch claims the slot, but Resolve only reports Active when
        // the stored snapshot is IsActive — an inactive snapshot under a held
        // connected latch falls through to Absent (no present latch claimed).
        var resolved = SingleUsb().ApplyConnected(MakeDevice(isActive: false)).Resolve();

        Assert.Equal(DeviceActivityStatus.Absent, resolved.ActivityStatus);
        Assert.Null(resolved.Device);
    }

    // ── Disconnected (soft-latch release) ──────────────────────────────

    [Fact]
    public void ApplyDisconnected_ConnectedOnly_ClearsToAbsent()
    {
        var device = MakeDevice();

        var resolved = SingleUsb()
            .ApplyConnected(device)
            .ApplyDisconnected(device)
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Absent, resolved.ActivityStatus);
        Assert.Null(resolved.Device);
    }

    [Fact]
    public void ApplyDisconnected_WhenAlsoPresent_KeepsPresentSnapshot()
    {
        // Bluetooth out-of-range: appeared (present latch) + connected, then
        // disconnect releases only the connected latch — present snapshot stays.
        var device = MakeDevice(category: DeviceCategory.Bluetooth);
        var state = DeviceTrackerResolution.Create(
            [Profile(f => f.OfCategory(DeviceCategory.Bluetooth))]);

        var resolved = state
            .ApplyAppeared(device)
            .ApplyConnected(device)
            .ApplyDisconnected(device)
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Present, resolved.ActivityStatus);
        Assert.Equal(device.Id, resolved.Device!.Id);
    }

    [Fact]
    public void ApplyDisconnected_ReleasesLatch_AllowsNextDeviceToClaim()
    {
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        var resolved = SingleUsb()
            .ApplyConnected(first)
            .ApplyDisconnected(first)
            .ApplyConnected(second)
            .Resolve();

        Assert.Equal(second.Id, resolved.Device!.Id);
        Assert.Equal(DeviceActivityStatus.Active, resolved.ActivityStatus);
    }

    [Fact]
    public void ApplyDisconnected_UnknownDevice_NoOp_ReturnsSameInstance()
    {
        var state = SingleUsb().ApplyConnected(MakeDevice(id: "USB\\1"));

        var next = state.ApplyDisconnected(MakeDevice(id: "USB\\UNKNOWN"));

        Assert.Same(state, next);
        Assert.Equal("USB\\1", next.Resolve().Device!.Id);
    }

    // ── Disappeared (present + connected release) ──────────────────────

    [Fact]
    public void ApplyDisappeared_ClearsPresent()
    {
        var device = MakeDevice();

        var resolved = SingleUsb()
            .ApplyAppeared(device)
            .ApplyDisappeared(device)
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Absent, resolved.ActivityStatus);
        Assert.Null(resolved.Device);
    }

    [Fact]
    public void ApplyDisappeared_CascadesFromActive_ClearsBoth()
    {
        var device = MakeDevice();

        var resolved = SingleUsb()
            .ApplyAppeared(device)
            .ApplyConnected(device)
            .ApplyDisappeared(device)
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Absent, resolved.ActivityStatus);
        Assert.Null(resolved.Device);
    }

    [Fact]
    public void ApplyDisappeared_ReleasesLatch_AllowsNextDevice()
    {
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        var resolved = SingleUsb()
            .ApplyAppeared(first)
            .ApplyDisappeared(first)
            .ApplyAppeared(second)
            .Resolve();

        Assert.Equal(second.Id, resolved.Device!.Id);
    }

    [Fact]
    public void ApplyDisappeared_UnknownDevice_NoOp_ReturnsSameInstance()
    {
        var state = SingleUsb().ApplyAppeared(MakeDevice(id: "USB\\1"));

        var next = state.ApplyDisappeared(MakeDevice(id: "USB\\UNKNOWN"));

        Assert.Same(state, next);
    }

    // ── Case-insensitive instance-id handling (DeviceId) ───────────────

    [Fact]
    public void Latch_SameIdDifferentCase_TreatedAsSameDevice()
    {
        // A device that re-enumerates with different casing must be recognised
        // as the same instance — not rejected as a second device. Routes through
        // DeviceId (OrdinalIgnoreCase). Regression for the Treehopper-reboot bug.
        var upper = MakeDevice(id: "USB\\VID_10C4&PID_8A7E\\JQ1KM1AI");
        var mixed = MakeDevice(id: "USB\\VID_10C4&PID_8A7E\\jQ1KM1Ai");

        var resolved = SingleUsb()
            .ApplyConnected(upper)
            .ApplyConnected(mixed) // same device, re-cased — refreshes, not rejected
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Active, resolved.ActivityStatus);
        Assert.Equal(mixed.Id, resolved.Device!.Id); // snapshot updated to the re-cased one
    }

    [Fact]
    public void ApplyDisappeared_MatchesLatchedIdCaseInsensitively()
    {
        var connected = MakeDevice(id: "USB\\ABC\\1");
        var goneRecased = MakeDevice(id: "usb\\abc\\1");

        var resolved = SingleUsb()
            .ApplyConnected(connected)
            .ApplyDisappeared(goneRecased) // different case → still clears the latch
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Absent, resolved.ActivityStatus);
        Assert.Null(resolved.Device);
    }

    // ── Multi-profile priority resolution ──────────────────────────────

    private static DeviceTrackerResolution PrimaryUsbFallbackBt(
        out DeviceProfile primary, out DeviceProfile fallback)
    {
        primary = Profile(f => f.OfCategory(DeviceCategory.Usb), name: "Primary");
        fallback = Profile(f => f.OfCategory(DeviceCategory.Bluetooth), name: "Fallback");
        return DeviceTrackerResolution.Create([primary, fallback]);
    }

    [Fact]
    public void MultiProfile_PrimaryConnects_ResolvesToPrimary()
    {
        var state = PrimaryUsbFallbackBt(out var primary, out _);
        var usb = MakeDevice(category: DeviceCategory.Usb);

        var resolved = state.ApplyConnected(usb).Resolve();

        Assert.Equal(usb.Id, resolved.Device!.Id);
        Assert.Same(primary, resolved.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_OnlyFallbackMatches_ResolvesToFallback()
    {
        var state = PrimaryUsbFallbackBt(out _, out var fallback);
        var bt = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth);

        var resolved = state.ApplyConnected(bt).Resolve();

        Assert.Equal(bt.Id, resolved.Device!.Id);
        Assert.Same(fallback, resolved.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_PrimaryOverridesFallback()
    {
        var state = PrimaryUsbFallbackBt(out var primary, out _);
        var bt = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth);
        var usb = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);

        var resolved = state
            .ApplyConnected(bt)
            .ApplyConnected(usb)
            .Resolve();

        Assert.Equal(usb.Id, resolved.Device!.Id);
        Assert.Same(primary, resolved.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_PrimaryDisconnects_FallsBackToFallback()
    {
        var state = PrimaryUsbFallbackBt(out _, out var fallback);
        var bt = MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth);
        var usb = MakeDevice(id: "USB\\1", category: DeviceCategory.Usb);

        var resolved = state
            .ApplyConnected(bt)
            .ApplyConnected(usb)
            .ApplyDisconnected(usb)
            .Resolve();

        Assert.Equal(bt.Id, resolved.Device!.Id);
        Assert.Same(fallback, resolved.ActiveProfile);
    }

    [Fact]
    public void MultiProfile_DeviceAssignedToHighestPriorityMatchingProfileOnly()
    {
        // A device matching two profiles is assigned to the highest-priority one
        // (the Apply* loop breaks after the first claim). Here a USB device
        // matches both a broad "any USB" primary and a precise VID/PID fallback;
        // it lands in primary.
        var primary = Profile(f => f.OfCategory(DeviceCategory.Usb), name: "AnyUsb");
        var fallback = Profile(f => f.WithUsbId("046D", "C52B"), name: "Precise");
        var state = DeviceTrackerResolution.Create([primary, fallback]);

        var resolved = state.ApplyConnected(MakeDevice()).Resolve();

        Assert.Same(primary, resolved.ActiveProfile);
    }

    // ── Property-changed snapshot refresh ──────────────────────────────

    [Fact]
    public void ApplyPropertyChanged_RefreshesStoredSnapshot()
    {
        var device = MakeDevice();
        var state = SingleUsb().ApplyConnected(device);

        var updated = device with { Name = "Updated Name" };
        var resolved = state.ApplyPropertyChanged(updated).Resolve();

        Assert.Equal("Updated Name", resolved.Device!.Name);
        Assert.Equal(DeviceActivityStatus.Active, resolved.ActivityStatus); // latch unchanged
    }

    [Fact]
    public void ApplyPropertyChanged_UnknownDevice_NoOp_ReturnsSameInstance()
    {
        var state = SingleUsb().ApplyConnected(MakeDevice(id: "USB\\1"));

        var updated = MakeDevice(id: "USB\\OTHER") with { Name = "Changed" };
        var next = state.ApplyPropertyChanged(updated);

        Assert.Same(state, next);
    }

    [Fact]
    public void ApplyPropertyChanged_DoesNotDisturbLatch()
    {
        // Refreshing the snapshot must not re-open or move the latch: a second
        // device still can't claim the slot after a property refresh.
        var first = MakeDevice(id: "USB\\1");
        var second = MakeDevice(id: "USB\\2");

        var resolved = SingleUsb()
            .ApplyConnected(first)
            .ApplyPropertyChanged(first with { Name = "Renamed" })
            .ApplyConnected(second) // still rejected — latch holds first
            .Resolve();

        Assert.Equal("USB\\1", resolved.Device!.Id);
        Assert.Equal("Renamed", resolved.Device!.Name);
    }

    // ── Replay (reconfigure path) ──────────────────────────────────────

    [Fact]
    public void ApplyReplay_ActiveDevice_ClaimsBothDimensions_ResolvesActive()
    {
        var resolved = SingleUsb().ApplyReplay(MakeDevice()).Resolve();

        Assert.Equal(DeviceActivityStatus.Active, resolved.ActivityStatus);
        Assert.NotNull(resolved.Device);
    }

    [Fact]
    public void ApplyReplay_InactiveDevice_ClaimsPresentOnly_ResolvesPresent()
    {
        var resolved = DeviceTrackerResolution
            .Create([Profile(f => f.OfCategory(DeviceCategory.Bluetooth))])
            .ApplyReplay(MakeDevice(category: DeviceCategory.Bluetooth, isActive: false))
            .Resolve();

        Assert.Equal(DeviceActivityStatus.Present, resolved.ActivityStatus);
        Assert.False(resolved.IsActive);
        Assert.True(resolved.IsPresent);
    }

    [Fact]
    public void ApplyReplay_NonMatching_NoOp_ReturnsSameInstance()
    {
        var state = SingleUsb();

        var next = state.ApplyReplay(MakeDevice(id: "BT\\1", category: DeviceCategory.Bluetooth));

        Assert.Same(state, next);
    }

    // ── Immutability ───────────────────────────────────────────────────

    [Fact]
    public void Transitions_DoNotMutateInputState()
    {
        // The whole point of the pure core: a transition returns a new value and
        // leaves the receiver untouched, so the prior state is still resolvable.
        var device = MakeDevice();
        var initial = SingleUsb();

        var afterConnect = initial.ApplyConnected(device);
        var afterDisconnect = afterConnect.ApplyDisconnected(device);

        // Each prior value resolves to what it was when produced.
        Assert.Equal(DeviceActivityStatus.Absent, initial.Resolve().ActivityStatus);
        Assert.Equal(DeviceActivityStatus.Active, afterConnect.Resolve().ActivityStatus);
        Assert.Equal(DeviceActivityStatus.Absent, afterDisconnect.Resolve().ActivityStatus);

        Assert.NotSame(initial, afterConnect);
        Assert.NotSame(afterConnect, afterDisconnect);
    }

    [Fact]
    public void Resolve_IsPure_SameStateYieldsEqualResult()
    {
        var state = SingleUsb().ApplyConnected(MakeDevice());

        var a = state.Resolve();
        var b = state.Resolve();

        Assert.Equal(a, b); // DeviceTrackerState is a record — value equality
    }

    // ── Full USB plug/unplug sequence ──────────────────────────────────

    [Fact]
    public void UsbSequence_PlugInThenUnplug_AbsentPresentActiveAbsent()
    {
        var device = MakeDevice();
        var state = SingleUsb();

        Assert.Equal(DeviceActivityStatus.Absent, state.Resolve().ActivityStatus);

        state = state.ApplyAppeared(device);
        Assert.Equal(DeviceActivityStatus.Present, state.Resolve().ActivityStatus);

        state = state.ApplyConnected(device);
        Assert.Equal(DeviceActivityStatus.Active, state.Resolve().ActivityStatus);

        state = state.ApplyDisappeared(device);
        Assert.Equal(DeviceActivityStatus.Absent, state.Resolve().ActivityStatus);
    }
}
