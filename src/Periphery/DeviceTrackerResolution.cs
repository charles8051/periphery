// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Collections.Immutable;

namespace Periphery;

/// <summary>
/// Pure, immutable transition core for <see cref="DeviceTracker"/>'s
/// per-profile latch + resolution logic (ADR-0006, ADR-0052 "functional core").
///
/// <para>This value owns the three pieces of state the tracker mutates as OS
/// device events arrive — the per-profile matched-device maps and the two
/// per-profile soft-latch slots (present and connected) — and nothing else:
/// no lock, no IO, no clock, no <see cref="System.Threading.Tasks.Task"/>, no
/// events. Each <c>Apply*</c> method is a total function
/// <c>(state, DeviceInfo) -&gt; state</c> returning a new immutable instance;
/// <see cref="Resolve"/> folds the latch state into the resolved
/// <see cref="DeviceTrackerState"/> (the matched device, its
/// <see cref="DeviceActivityStatus"/>, and the <see cref="DeviceProfile"/> that
/// produced it).</para>
///
/// <para>The shell (<see cref="DeviceTracker"/>) keeps the lock, a single cell
/// holding the current <see cref="DeviceTrackerResolution"/>, and the
/// notification surface: each <c>internal On*</c> method takes the lock, applies
/// the matching pure transition, swaps the cell, resolves, and raises the same
/// notifications as before. The latch rules are unchanged from the fused
/// implementation this replaced — see ADR-0006 §3 (resolution) and §9
/// (soft-latch by <see cref="DeviceInfo.Id"/>, per profile, per dimension).</para>
/// </summary>
/// <remarks>
/// <para><b>Latch keys are case-insensitive.</b> Device instance ids are
/// compared and keyed through <see cref="DeviceId"/>, whose own
/// <see cref="DeviceId.Equals(DeviceId)"/>/<c>==</c> are ordinal-ignore-case, so a
/// device that re-enumerates with different casing is still recognised as the same
/// instance — the invariant lives in the <see cref="DeviceId"/> value itself. The
/// per-profile matched-device maps are keyed by <see cref="DeviceId"/>, so the
/// default <see cref="EqualityComparer{T}"/> already keys them case-insensitively
/// with no per-call-site comparer wiring.</para>
/// <para><b>Profiles are keyed by reference.</b> The latch and device maps are
/// keyed on the <see cref="DeviceProfile"/> instances passed to
/// <see cref="Create"/>; resolution walks them in the supplied (priority)
/// order. This matches the prior dictionary-keyed-by-profile-reference
/// behaviour.</para>
/// </remarks>
internal sealed class DeviceTrackerResolution
{
    /// <summary>Profiles in descending priority order (resolution scan order).</summary>
    private readonly ImmutableArray<DeviceProfile> _profiles;

    /// <summary>
    /// Per-profile matched-device snapshots, keyed by
    /// <see cref="DeviceInfo.Id"/> (case-insensitive via <see cref="DeviceId"/>).
    /// At most one entry per profile in normal operation — the latch rejects a
    /// second device.
    /// </summary>
    private readonly ImmutableDictionary<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>> _devicesByProfile;

    /// <summary>Per-profile present-dimension soft latch (latched id, or null).</summary>
    private readonly ImmutableDictionary<DeviceProfile, DeviceId?> _presentLatch;

    /// <summary>Per-profile connected-dimension soft latch (latched id, or null).</summary>
    private readonly ImmutableDictionary<DeviceProfile, DeviceId?> _connectedLatch;

    private DeviceTrackerResolution(
        ImmutableArray<DeviceProfile> profiles,
        ImmutableDictionary<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>> devicesByProfile,
        ImmutableDictionary<DeviceProfile, DeviceId?> presentLatch,
        ImmutableDictionary<DeviceProfile, DeviceId?> connectedLatch)
    {
        _profiles = profiles;
        _devicesByProfile = devicesByProfile;
        _presentLatch = presentLatch;
        _connectedLatch = connectedLatch;
    }

    /// <summary>
    /// Build the empty resolution state for a priority-ordered profile list:
    /// no matched devices, both latch slots clear for every profile. The
    /// resolved view of this state is <see cref="DeviceActivityStatus.Absent"/>
    /// (nothing matched) — but a freshly-constructed tracker stays
    /// <see cref="DeviceActivityStatus.Unknown"/> until it first resolves,
    /// which is the shell's concern (it does not call <see cref="Resolve"/>
    /// at construction). See ADR-0056.
    /// </summary>
    public static DeviceTrackerResolution Create(IReadOnlyList<DeviceProfile> profiles)
    {
        var profileArray = ImmutableArray.CreateRange(profiles);

        var devicesBuilder = ImmutableDictionary.CreateBuilder<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>>();
        var presentBuilder = ImmutableDictionary.CreateBuilder<DeviceProfile, DeviceId?>();
        var connectedBuilder = ImmutableDictionary.CreateBuilder<DeviceProfile, DeviceId?>();

        // DeviceId's default equality is OrdinalIgnoreCase, so the device maps
        // key case-insensitively without an explicit comparer.
        var emptyDeviceMap = ImmutableDictionary.Create<DeviceId, DeviceInfo>();
        foreach (var profile in profileArray)
        {
            devicesBuilder[profile] = emptyDeviceMap;
            presentBuilder[profile] = null;
            connectedBuilder[profile] = null;
        }

        return new DeviceTrackerResolution(
            profileArray,
            devicesBuilder.ToImmutable(),
            presentBuilder.ToImmutable(),
            connectedBuilder.ToImmutable());
    }

    // ── Transitions ────────────────────────────────────────────────────

    /// <summary>
    /// A matching device entered the OS device tree. Claims the highest-priority
    /// matching profile's present-latch slot (if free, or already held by this
    /// id) and records the snapshot. The <c>break</c> assigns the device to at
    /// most one profile. Non-matching / already-claimed-by-another profiles are
    /// left untouched — returns the same instance if nothing changed.
    /// </summary>
    public DeviceTrackerResolution ApplyAppeared(DeviceInfo device)
    {
        foreach (var profile in _profiles)
        {
            if (!profile.Filter.Matches(device)) continue;
            if (_presentLatch[profile] is { } id && id != device.Id) continue;

            var presentLatch = _presentLatch[profile] is null
                ? _presentLatch.SetItem(profile, device.Id)
                : _presentLatch;
            var devicesByProfile = SetDevice(profile, device);

            return With(devicesByProfile: devicesByProfile, presentLatch: presentLatch);
        }

        return this;
    }

    /// <summary>
    /// A matching device became active (driver started / in range). Claims the
    /// highest-priority matching profile's connected-latch slot and records the
    /// snapshot. Mirrors <see cref="ApplyAppeared"/> on the connected dimension.
    /// </summary>
    public DeviceTrackerResolution ApplyConnected(DeviceInfo device)
    {
        foreach (var profile in _profiles)
        {
            if (!profile.Filter.Matches(device)) continue;
            if (_connectedLatch[profile] is { } id && id != device.Id) continue;

            var connectedLatch = _connectedLatch[profile] is null
                ? _connectedLatch.SetItem(profile, device.Id)
                : _connectedLatch;
            var devicesByProfile = SetDevice(profile, device);

            return With(devicesByProfile: devicesByProfile, connectedLatch: connectedLatch);
        }

        return this;
    }

    /// <summary>
    /// A matching device became inactive. Releases the connected-latch slot it
    /// held; keeps the snapshot iff the present latch still holds the same id
    /// (Bluetooth present-not-connected), otherwise releases the slot entirely.
    /// </summary>
    public DeviceTrackerResolution ApplyDisconnected(DeviceInfo device)
    {
        foreach (var profile in _profiles)
        {
            if (_connectedLatch[profile] != device.Id) continue;

            var connectedLatch = _connectedLatch.SetItem(profile, null);
            var devicesByProfile = _presentLatch[profile] == device.Id
                ? SetDevice(profile, device)        // keep snapshot (BT present-not-connected)
                : RemoveDevice(profile, device.Id); // no appearance basis; release slot

            return With(devicesByProfile: devicesByProfile, connectedLatch: connectedLatch);
        }

        return this;
    }

    /// <summary>
    /// A matching device left the OS device tree. Removes its snapshot from the
    /// owning profile and clears whichever latch slots held its id.
    /// </summary>
    public DeviceTrackerResolution ApplyDisappeared(DeviceInfo device)
    {
        foreach (var profile in _profiles)
        {
            if (!_devicesByProfile[profile].ContainsKey(device.Id)) continue;

            var devicesByProfile = RemoveDevice(profile, device.Id);
            var presentLatch = _presentLatch[profile] == device.Id
                ? _presentLatch.SetItem(profile, null)
                : _presentLatch;
            var connectedLatch = _connectedLatch[profile] == device.Id
                ? _connectedLatch.SetItem(profile, null)
                : _connectedLatch;

            return With(devicesByProfile, presentLatch, connectedLatch);
        }

        return this;
    }

    /// <summary>
    /// Replay-time variant rolling <see cref="ApplyAppeared"/> +
    /// <see cref="ApplyConnected"/> into one for a single device from the
    /// watcher's known-device snapshot (used during reconfigure replay).
    /// Claims the present slot, and — only when the device is active — the
    /// connected slot too, on the same matching profile.
    /// </summary>
    public DeviceTrackerResolution ApplyReplay(DeviceInfo device)
    {
        foreach (var profile in _profiles)
        {
            if (!profile.Filter.Matches(device)) continue;

            if (_presentLatch[profile] is { } presentId && presentId != device.Id)
                continue;

            var presentLatch = _presentLatch[profile] is null
                ? _presentLatch.SetItem(profile, device.Id)
                : _presentLatch;
            var devicesByProfile = SetDevice(profile, device);

            var connectedLatch = _connectedLatch;
            if (device.IsActive &&
                (_connectedLatch[profile] is not { } connId || connId == device.Id))
            {
                if (_connectedLatch[profile] is null)
                    connectedLatch = _connectedLatch.SetItem(profile, device.Id);
            }

            return With(devicesByProfile, presentLatch, connectedLatch);
        }

        return this;
    }

    /// <summary>
    /// Refresh a device snapshot in place: replace the stored
    /// <see cref="DeviceInfo"/> for the first profile whose matched-device map
    /// contains <paramref name="current"/>'s id, leaving every latch slot
    /// untouched. The shell calls this only when the changed device is the
    /// resolved one; whether to re-resolve is the shell's decision (it depends
    /// on the prior resolved view, not on the latch state). Returns the same
    /// instance when no profile holds the id.
    /// </summary>
    public DeviceTrackerResolution ApplyPropertyChanged(DeviceInfo current)
    {
        foreach (var profile in _profiles)
        {
            if (!_devicesByProfile[profile].ContainsKey(current.Id)) continue;
            return With(devicesByProfile: SetDevice(profile, current));
        }

        return this;
    }

    // ── Resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Fold the latch state into a resolved <see cref="DeviceTrackerState"/>:
    /// the highest-priority profile with an active connected-latched device
    /// wins as <see cref="DeviceActivityStatus.Active"/>; failing that, the
    /// highest-priority profile with a present-latched device wins as
    /// <see cref="DeviceActivityStatus.Present"/>; failing that,
    /// <see cref="DeviceActivityStatus.Absent"/>. Per-profile latching
    /// guarantees at most one device per profile map, so each lookup is a
    /// single entry.
    /// </summary>
    public DeviceTrackerState Resolve()
    {
        foreach (var profile in _profiles)
        {
            if (_connectedLatch[profile] is { } connId &&
                _devicesByProfile[profile].TryGetValue(connId, out var d) &&
                d.IsActive)
            {
                return new DeviceTrackerState(d, DeviceActivityStatus.Active, profile);
            }
        }

        foreach (var profile in _profiles)
        {
            if (_presentLatch[profile] is { } presId &&
                _devicesByProfile[profile].TryGetValue(presId, out var d))
            {
                return new DeviceTrackerState(d, DeviceActivityStatus.Present, profile);
            }
        }

        return new DeviceTrackerState(null, DeviceActivityStatus.Absent, null);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private ImmutableDictionary<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>> SetDevice(
        DeviceProfile profile, DeviceInfo device) =>
        _devicesByProfile.SetItem(profile, _devicesByProfile[profile].SetItem(device.Id, device));

    private ImmutableDictionary<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>> RemoveDevice(
        DeviceProfile profile, DeviceId id) =>
        _devicesByProfile.SetItem(profile, _devicesByProfile[profile].Remove(id));

    private DeviceTrackerResolution With(
        ImmutableDictionary<DeviceProfile, ImmutableDictionary<DeviceId, DeviceInfo>>? devicesByProfile = null,
        ImmutableDictionary<DeviceProfile, DeviceId?>? presentLatch = null,
        ImmutableDictionary<DeviceProfile, DeviceId?>? connectedLatch = null) =>
        new(
            _profiles,
            devicesByProfile ?? _devicesByProfile,
            presentLatch ?? _presentLatch,
            connectedLatch ?? _connectedLatch);
}
