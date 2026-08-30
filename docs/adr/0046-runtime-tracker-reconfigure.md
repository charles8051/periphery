---
title: "ADR-0046: Runtime DeviceTracker Reconfigure"
status: "Accepted"
status_note: "Shipped - `DeviceTracker.Reconfigure` and `ReplaceProfiles`. Frontmatter previously read `Proposed` while the body read `Accepted`."
date: "2026-05-24"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "device-tracker", "device-watcher", "dynamic-config"]
supersedes: ""
superseded_by: ""
---

# ADR-0046: Runtime DeviceTracker Reconfigure

## Context

`DeviceTracker`'s filter is set at construction and immutable for the
tracker's lifetime. `DeviceWatcher.AddTracker` is gated on
`ThrowIfStarted()` — once the watcher has begun, the tracker set is
locked. This is the right default: most consumers configure trackers
at startup from declarative config, hand the watcher to a hosted
service, and never touch the topology again.

The first consumer that breaks the assumption is **the kiosk consumer**,
which has a fleet of identical-hardware kiosks where two cameras share
USB VID/PID. The disambiguator (Windows `ContainerId`) is unique per
physical USB connection but unique per *kiosk instance* — the
role-to-`ContainerId` mapping is per-machine provisioning data that
can't be checked into source control.

Today, changing a tracker's filter requires:
1. Edit `appsettings.Local.json` to update the role's
   `ProfileDefinition.ContainerId`
2. Restart the kiosk process

For a single dev box this is fine. For a fleet of kiosks being
provisioned in the field — assigning roles, swapping cameras, moving
hardware between USB ports — it's friction. The dashboard's
provisioning workflow wants to:

1. Operator clicks "Assign FrontCamera to this candidate camera"
2. Tracker rebinds live to the new device
3. The camera service tears down its current session and re-opens
   against the new device
4. Operator sees preview switch in real time

Without runtime reconfigure, every assignment is a write-restart-verify
loop. With it, provisioning becomes interactive.

### Why `DeviceTracker` and not just dispose-and-recreate

A consumer could dispose the old tracker and create a new one with
the new filter, but the surrounding wiring resists this:

- Consumers hold references to the tracker (camera facades, lighting
  facades, the printer facade, debug VMs). Dropping the instance
  underneath them is a footgun.
- `DeviceWatcher.AddTracker` is sealed once started. Adding a fresh
  tracker to a running watcher is not supported by the existing API
  contract.
- The tracker holds `_observers` (Rx-style subscribers) and event
  handlers. Recreating means re-subscribing every consumer, which
  reaches into every facade.

A reconfigure-in-place keeps the tracker reference stable across
filter changes — consumers' subscriptions, event handlers, and
references all stay valid.

## Decision

**Add two public methods to `DeviceTracker`:**

```csharp
public void Reconfigure(Action<DeviceFilter> configure);
public void ReplaceProfiles(params DeviceProfile[] profiles);
```

The first is the single-profile equivalent of the
`DeviceTracker(Action<DeviceFilter>, string?)` constructor; the second
is the multi-profile equivalent of
`DeviceTracker(string?, params DeviceProfile[])`. Both replace the
tracker's profile set atomically with no change to the tracker's
identity, event handlers, or `IObserver<DeviceTrackerState>`
subscriptions.

**Semantics:**

1. The new filter must have `HasAnyCriteria == true` (same validation
   as construction).
2. Replacement is atomic under the tracker's existing `_lock`. No
   intermediate state is observable to subscribers.
3. After the swap, the tracker re-evaluates against the watcher's
   current device snapshot (via the new internal
   `DeviceWatcher.ReplayKnownDevicesTo(DeviceTracker)`).
4. `StateChanged` fires **at most once** per `Reconfigure` /
   `ReplaceProfiles` call, with the new snapshot. The single fire
   happens even when the resolved device is unchanged — each
   reconfigure constructs a new `DeviceProfile` instance, so the
   tracker's `NotifyChanges` reference-inequality check on
   `ActiveProfile` evaluates true. Consumers that need to detect
   actual device-identity rebinds should compare `Device.Id` in
   their handler rather than subscribing blindly to `StateChanged`.
   The "at most once per reconfigure" invariant is what matters
   operationally — no replay-of-events fan-out for the intermediate
   per-device appearances during the replay.
5. The existing `Appeared` / `Disappeared` / `Activated` /
   `Deactivated` edge events fire based on the resolved tracker's
   net `IsPresent` / `IsActive` transition — *not* on bind-identity
   changes caused by the reconfigure. A reconfigure that swaps the
   bound device from camera A (Active) to camera B (Active) does
   **not** fire `Disappeared` for A or `Appeared` for B: both
   `before` and `after` have `IsPresent == true`, so neither edge
   event fires. The single `StateChanged` emission with the new
   snapshot is the signal consumers use to detect device-identity
   changes. A reconfigure that drops the binding entirely
   (`Active → Absent`) fires `Disappeared` + `Deactivated`; one
   that creates a fresh binding (`Absent → Active`) fires
   `Appeared` + `Activated`. The semantic of the edge events stays
   "OS-observed transition" — reconfigure just changes which
   transitions the tracker sees.
6. Reconfigure on an unbound tracker (one not yet `Bind`-ed to a
   watcher) is legal — it updates internal filter state, the new
   filter takes effect at the next `Bind`.

**`DeviceWatcher` gains an internal `ReplayKnownDevicesTo(DeviceTracker)`**
that iterates the watcher's existing `_deviceCache` snapshot and pushes
each device through a new internal tracker hook
(`ReplayDeviceInternal(DeviceInfo)`) that updates latches +
`_devicesByProfile` directly without invoking the per-event
`NotifyChanges` path. This avoids fan-out of N intermediate StateChanged
events during the reconfigure.

## Consequences

### Positive

- **POS-001**: Consumers can react to tracker rebinding live. The
  kiosk's `SwitchableFrontCameraService` (and siblings) already have
  the OnBefore/OnAfter teardown/rebuild hooks for mock-toggle —
  subscribing to `tracker.StateChanged` for device-identity changes
  triggers the same rebuild path. No new facade infrastructure.
- **POS-002**: Provisioning UX collapses from edit-restart-verify to
  edit-and-watch-it-happen. Significant operational improvement for
  fleet rollouts.
- **POS-003**: Tracker identity stays stable across reconfigures.
  Consumer references, event handlers, and Rx subscriptions remain
  valid; no facade-level rewiring needed.
- **POS-004**: The change is opt-in. Consumers that never call
  `Reconfigure` see no behavioural difference. No semver-level break.

### Negative

- **NEG-001**: A reconfigure that drops the currently-bound device
  and finds no replacement leaves the tracker briefly in
  `Absent`/`Present`-fallback state before the new filter (if it
  matches something else) resolves. The single batched `StateChanged`
  hides this, but consumers that read individual properties between
  the reconfigure's two locked phases would see inconsistency. Mitigated
  by the property getters' `lock (_lock)` — they wait for the
  reconfigure to complete before returning a value.
- **NEG-002**: `Reconfigure` doesn't replay `Appeared` / `Activated`
  events for every device the new filter newly matches — only the
  resolved-device transition. Consumers expecting per-device fanout
  on reconfigure (rare; the kiosk doesn't) would need a different
  pattern.
- **NEG-003**: Adds two methods to a class that today has a very
  small public surface. Risk of API bloat. Mitigated by these being
  the symmetric counterparts of the two constructors — same
  vocabulary, same semantics, just at a later point in time.

## Alternatives Considered

### A — `DeviceFilter.Reconfigure` (mutate the filter in place)

- **Description**: Make `DeviceFilter`'s structured properties
  mutable from public API. Consumers update the filter; the tracker
  observes via change-notification.
- **Rejection reason**: `DeviceFilter` is currently a configuration
  delegate target, not a stateful object. Making it observable
  expands its responsibility surface considerably. Also: re-evaluation
  against the device cache still needs to be a method somewhere; the
  filter doesn't know about the watcher's known-device set.

### B — Dispose-and-recreate at consumer level

- **Description**: Consumers (`SwitchableFrontCameraService`,
  `DeviceTrackingService`, etc.) dispose the old tracker and create
  a new one with the new filter.
- **Rejection reason**: Detailed in the Context section above —
  reference invalidation, watcher's `ThrowIfStarted` lock,
  subscription rewiring. Forcing the migration cost onto every
  consumer of trackers is worse than absorbing it once in
  `DeviceTracker` + `DeviceWatcher`.

### C — Restart-required tracker config

- **Description**: Document that tracker config changes require a
  process restart. Use a "restart pending" banner in consumer apps;
  defer to operator-driven restart.
- **Rejection reason**: Works fine for one-off changes, but fleet
  provisioning involves frequent role-assignment churn (multiple
  swaps per kiosk during commissioning). Restart-per-change is
  acceptable friction for editing `FontSizePoints`; it's not for
  "assign each of two cameras to a role." The kiosk's existing
  `IRestartPendingTracker` mechanism still applies for non-tracker
  restart-required changes; this ADR addresses the specific case
  where live reconfigure pays off.

## Implementation Notes

- **IMP-001**: `DeviceTracker._profiles` changes from
  `IReadOnlyList<DeviceProfile>` (compile-time array) to
  `List<DeviceProfile>` to support mutation. All mutations happen
  under `_lock`.
- **IMP-002**: `Reconfigure(Action<DeviceFilter>)` builds a new
  `DeviceFilter`, validates `HasAnyCriteria`, builds a single-element
  `DeviceProfile` list, then delegates to the same private apply path
  as `ReplaceProfiles`.
- **IMP-003**: `ReplaceProfiles(params DeviceProfile[])` validates
  non-null + non-empty (same as the multi-profile constructor), then
  swaps `_profiles` + reinitialises latches + replays the watcher's
  device cache.
- **IMP-004**: `DeviceWatcher.ReplayKnownDevicesTo(DeviceTracker)`
  takes a snapshot of `_deviceCache` (under its lock, copied to an
  array) and iterates outside the cache lock to avoid holding two
  locks. The tracker's `_lock` is held throughout the iteration —
  acquired by the calling `Reconfigure`.
- **IMP-005**: New internal method
  `DeviceTracker.ReplayDeviceInternal(DeviceInfo)` applies the same
  latch logic as `OnDeviceAppeared` + `OnDeviceConnected` (depending
  on `DeviceInfo.IsActive`) but without `NotifyChanges`. Reconfigure
  then captures the after-state once and notifies once.
- **IMP-006**: `Reconfigure` on an unbound tracker (`_owner is null`)
  just updates `_profiles` and resets latches — the next `Bind` will
  drive the initial enumeration through the standard
  `OnDeviceAppeared` path.
- **IMP-007**: Update `PublicAPI.Unshipped.txt` with the two new
  public methods.
- **IMP-008**: Tests in `Periphery.Tests`:
  - Reconfigure rebinds when new filter matches a different device
  - Reconfigure to no-match leaves tracker `Absent`
  - Reconfigure to same-match is a no-op (no `StateChanged` fire)
  - StateChanged fires exactly once per reconfigure
  - `Appeared`/`Disappeared` fire for the net device transition
  - Concurrent device-arrival event during reconfigure is serialised
    (no double-update)
  - Reconfigure with null configure throws `ArgumentNullException`
  - Reconfigure with empty filter throws `ArgumentException`
  - Reconfigure on unbound tracker is legal
