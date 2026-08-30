---
title: "ADR-0006: DeviceProfile and Single-Device Resolution"
status: "Accepted"
status_note: "Shipped - `DeviceProfile`, `DeviceTrackerResolution`, `ActiveProfile`. The `IsAmbiguous` flag described here was not built; ambiguity surfaces as an unresolved tracker."
date: "2025-07-17"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: "0001-device-tracking-handles.md"
superseded_by: ""
depends_on: ["0001-device-tracking-handles.md", "0004-two-level-device-state-model.md"]
---

# ADR-0006: DeviceProfile and Single-Device Resolution

**Supersedes:** ADR-0001 §3 (set-tracking model), ADR-0001 §6 (API shape / public surface sketch)  
**Depends on:** ADR-0001 (Device Tracking Handles), ADR-0004 (Two-Level State Model)

---

## Context

ADR-0001 §3 established **set tracking** as the model for `DeviceTracker`: `IsPresent` and `IsConnected` mean "at least one matching device," and `PresentDevices` / `ConnectedDevices` expose the full set of all matching devices. This was the correct general model for broad queries like "any connected USB device" or "any Logitech peripheral."

Three problems have emerged as consumers try to use `DeviceTracker` at the application level:

1. **No single confident reference.** A consumer who filters precisely for one device (e.g. `WithUsbId("046D", "C52B")`) gets back `ConnectedDevices` — a list that is _probably_ one item but could be two (two identical mice plugged in). The consumer must read `.Count` and decide what to do with the list. There is no `Device` property that simply says "this is the device, or null."

2. **No fallback priority.** A consumer who wants "use the production device; if absent, accept the compatible model; if absent, use the dev board" cannot express this with a single `DeviceFilter`. The only workarounds are three separate trackers (which adds watcher overhead) or a custom lambda that embeds priority logic (which produces only a bool, not a ranked reference).

3. **No conflict signal.** If two devices match the same precise filter — a hardware conflict the consumer did not anticipate — `ConnectedDevices.Count` silently becomes 2 with no structured indication that the result is ambiguous. `IsConnected` remains `true`, so callbacks proceed as if nothing is wrong.

---

## Decision Drivers

- **Single `DeviceInfo?` reference** — `tracker.Device` should be null or a single unambiguous match; never a set.
- **Ordered fallback** — multiple candidate filters, tried in priority order. The highest-priority filter with exactly one connected match wins.
- **Conflict as structured state** — when multiple devices match the winning filter, expose this as a readable, bindable property, not a silent set.
- **Preserve notification surfaces** — `INotifyPropertyChanged`, `StateChanged`, and `IObservable<bool>` all survive unchanged in contract.
- **No provider changes** — the fan-out logic in `DeviceWatcher` is unchanged. All modifications are internal to `DeviceTracker`.

---

## Analysis

### 1. New Type: `DeviceProfile`

A `DeviceProfile` wraps a `DeviceFilter` and an optional diagnostic name. It is the unit of "one candidate in a priority-ordered list."

```csharp
public sealed class DeviceProfile
{
    public DeviceProfile(Action<DeviceFilter> configure, string? name = null)
    public string? Name { get; }
    internal DeviceFilter Filter { get; }
}
```

The full `DeviceFilter` fluent API is available through the `configure` delegate. No new filter methods are required.

**Why not pass `DeviceFilter` directly?** `DeviceFilter`'s constructor is `internal` — public construction only happens through `DeviceWatcher.Track()` or `new DeviceTracker(configure)`. `DeviceProfile` maintains that encapsulation boundary while adding a public name and a future extension point for per-profile options (see Open Question 2).

**Why a separate type and not an anonymous record or tuple?** `DeviceProfile` can appear in diagnostics, XAML bindings (`ActiveProfile.Name`), and config deserialization. A named type with explicit semantics is clearer than `(Action<DeviceFilter> filter, string? name)`.

---

### 2. Internal Storage — Per-Profile Sets

The two flat lists are replaced with per-profile dictionaries. The tracker now maintains separate device sets for each profile, keyed by profile reference.

```
// Before
DeviceFilter _filter
List<DeviceInfo> _presentDevices
List<DeviceInfo> _connectedDevices

// After
IReadOnlyList<DeviceProfile> _profiles
Dictionary<DeviceProfile, List<DeviceInfo>> _presentByProfile
Dictionary<DeviceProfile, List<DeviceInfo>> _connectedByProfile
```

`DeviceWatcher.FanOut*` is unchanged — it still calls `tracker.Matches(device)` and `tracker.OnDeviceConnected(device)` etc. Inside `OnDeviceConnected`, the tracker iterates all profiles, adds the device to every matching profile's list, then calls `Resolve()`.

**Note:** A device can match multiple profiles simultaneously. For example, a device might match both "any Logitech device" (profile[0]) and "USB VID 046D/PID C52B" (profile[1]). The device is added to every matching profile's set. Resolution (§3) picks the highest-priority unambiguous result. This is correct and expected behaviour.

---

### 3. Resolution Algorithm

After every state update, a `Resolve()` pass computes three derived values: `_device`, `_activeProfile`, and `_ambiguousDevices`. The pass iterates profiles in priority order:

```
for each profile in priority order:
    connected = _connectedByProfile[profile]
    if connected.Count == 1
        Device = connected[0], ActiveProfile = profile, IsAmbiguous = false  ← STOP
    if connected.Count > 1
        Device = null, ActiveProfile = profile, IsAmbiguous = true           ← STOP

Device = null, ActiveProfile = null, IsAmbiguous = false   ← no profile matched
```

**Ambiguity stops resolution at the colliding profile.** It does not fall through to a lower-priority profile.

**Note on normal operation:** Per-profile latching (§9) prevents `Count > 1` from occurring in practice — the second device's arrival is rejected at the latch check before it enters the set. The `Count > 1` branch is a safety net for the simultaneous-arrival race condition.

**Rationale for stopping:** If two production devices match profile[0], silently activating the dev-board profile[2] would be wrong and confusing. The collision is actionable information for the caller — "you have two matching devices, please unplug one." Falling through would hide the conflict entirely. Consumers who want a fallthrough on ambiguity can achieve it today with separate trackers.

**Present-state resolution** follows the same algorithm on `_presentByProfile`, producing `PresentDevice?`. The `IsPresent` property is equivalent to `PresentDevice != null`.

---

### 4. Ambiguity Communication — Property, Not Exception

Three options:

| Option | Mechanism | Catchable by caller? | XAML-bindable? | Resolves when device unplugged? |
|---|---|---|---|---|
| **A. `IsAmbiguous` property** | State on tracker | N/A — not thrown | ✅ Yes | ✅ Yes — state transitions back |
| **B. Throw exception** | `InvalidOperationException` on second connect | ❌ No — fires on OS event thread | ❌ No | ❌ No — can't "unthrow" |
| **C. `Debug.Assert` only** | Assertion in debug builds | ❌ No | ❌ No | ❌ No |

**Decision: Option A — `IsAmbiguous` + `AmbiguousDevices`.**

**Why not throw:** Device connection events arrive on OS callback threads. No user `try/catch` block is present at that call site. `StateChanged` subscriptions do not have an exception boundary that would let the caller catch and recover. Furthermore, two identical devices being plugged in simultaneously is a valid hardware state (a lab with two identical keyboards, a hot-swap scenario), not a programming error. Throwing would crash the event handler with no path to recovery.

**Why not `Debug.Assert` only:** Assertions are stripped in release builds. A signal that only fires in debug is not a signal the application can rely on in production.

**`Device` returns `null` when `IsAmbiguous`.** This enforces the single-reference contract — callers cannot accidentally use an ambiguous device just because it happens to be first in the list. The caller must explicitly handle the ambiguous case to get any device reference.

`IsAmbiguous` participates in `StateChanged` and `PropertyChanged` notifications like any other property. `IObservable<bool>` continues to push `IsConnected` transitions only.

---

### 5. IsPresent Semantics

Two options:

| Option | `IsPresent` | `PresentDevice` |
|---|---|---|
| **A. Symmetric with connected** | `PresentDevice != null` (same priority/ambiguity logic) | `DeviceInfo?` — highest-priority unambiguous present device |
| **B. Coarse** | "any profile has at least one present device" | Absent or best-effort |

**Decision: Option A — symmetric resolution.** `IsPresent` and `IsConnected` both derive from the same per-profile resolution algorithm, applied to `_presentByProfile` and `_connectedByProfile` respectively. This is predictable and consistent — the semantics are identical, just over different sets.

**USB implication:** For USB devices, present and connected are equivalent (plug in = both appear simultaneously). `PresentDevice` and `Device` resolve to the same result in practice. The distinction is meaningful only for Bluetooth (paired but out-of-range = present but not connected) and Network (adapter disabled = present but not connected).

---

### 6. Constructor API

The existing single-profile constructor is preserved in signature. It wraps `configure` in an anonymous `DeviceProfile` and delegates to the multi-profile constructor:

```csharp
// Single-profile — signature unchanged, existing call sites compile unmodified
public DeviceTracker(Action<DeviceFilter> configure, string? name = null)

// Multi-profile — ordered, highest priority first
public DeviceTracker(string? name, params DeviceProfile[] profiles)
```

The tracker's `Name` is distinct from profile names. A tracker named `"Mouse"` may hold profiles named `"MX Master"`, `"M705"`, and `"Dev HID"`.

`DeviceWatcher.Track(Action<DeviceFilter>, string?)` is **unchanged in signature** — it still creates a `DeviceTracker` internally. For multi-profile trackers, the caller constructs the `DeviceTracker` directly and passes it via the existing `Track(params DeviceTracker[])` overload.

---

### 7. Removed and Replaced Public Members

| Before | After | Notes |
|---|---|---|
| `ConnectedDevices` (`IReadOnlyList<DeviceInfo>`) | `Device` (`DeviceInfo?`) | Single resolved connected device; null if nothing matches or top match is ambiguous |
| `PresentDevices` (`IReadOnlyList<DeviceInfo>`) | `PresentDevice` (`DeviceInfo?`) | Single resolved present device; same resolution semantics |
| `IsConnected`: `_connectedDevices.Count > 0` | `Device != null` | For single-profile unambiguous case, semantically equivalent |
| `IsPresent`: `_presentDevices.Count > 0` | `PresentDevice != null` | Same |
| _(absent)_ | `ActiveProfile` (`DeviceProfile?`) | Which profile resolved — useful for "currently using fallback" diagnostics |
| _(absent)_ | `IsAmbiguous` (`bool`) | Top-matching profile has > 1 connected device |
| _(absent)_ | `AmbiguousDevices` (`IReadOnlyList<DeviceInfo>`) | The conflicting devices when `IsAmbiguous` is true |

**This is a breaking change.** `PresentDevices` and `ConnectedDevices` are removed. Any consumer iterating or reading `.Count` on these properties will not compile. In-library impact: `DeviceTrackerTests.cs` (all tests referencing the removed properties) and `Program.cs` (examples 12–13).

---

### 8. Fan-Out and DeviceWatcher Impact

`DeviceWatcher` requires **no changes** at the fan-out level. The four `FanOut*` methods call `tracker.Matches(device)` and the appropriate `tracker.OnDevice*()` method, identical to today.

`tracker.Matches(device)` changes from a single-filter check to an any-profile check:

```csharp
// Before
internal bool Matches(DeviceInfo device) => _filter.Matches(device);

// After
internal bool Matches(DeviceInfo device) => _profiles.Any(p => p.Filter.Matches(device));
```

The `On*` methods route each device to its matching profile(s) before calling `Resolve()`.

---

### 9. Per-Profile Device Latching

Without an additional mechanism, the resolution algorithm in §3 re-evaluates all profiles against their per-profile device sets on every event. This means a second device that matches a profile's filter after the profile is already resolved can change the resolved state — either triggering `IsAmbiguous` or silently replacing the first device when it disconnects. This is correct for broad "any Logitech device" trackers, but wrong for single-device identification: if an application has claimed profile[0]'s device, a second identical device appearing should not disturb that claim.

**Decision: soft latch by `DeviceInfo.Id` per profile, per state dimension.**

Each profile maintains two independent latch slots — one for the present-state dimension and one for the connected-state dimension:

```
_presentLatch[profile]:   string?   // latched DeviceInfo.Id for present set
_connectedLatch[profile]: string?   // latched DeviceInfo.Id for connected set
```

**On device arrival (appeared / connected):**

```
for each profile in priority order:
    if profile.Filter does not match device: continue
    if profile latch is set AND latch != device.Id: continue  ← already claimed by different device
    if profile latch is null: set latch = device.Id           ← first arrival claims the slot
    add device to profile's set
    break                                                     ← device assigned; stop searching
```

The `break` ensures each device is assigned to exactly one profile — the highest-priority profile whose filter matches and whose latch slot is available.

**On device removal (disconnected / disappeared):**

```
for each profile (no early break — device may exist in any profile's set):
    remove device from profile's set if present
    if profile latch == device.Id: clear latch
```

Removal scans all profiles because the present and connected dimensions are independent — a device could theoretically be in different profiles' sets if its filter overlap changed mid-session. Clearing the latch on removal means the slot is available again for the next matching device.

**Why `DeviceInfo.Id` and not `SerialNumber`:**

| Key | Always present? | Always unique? | Port-stable? |
|---|---|---|---|
| `DeviceInfo.Id` (OS instance ID) | ✅ Yes | ✅ Yes | ❌ No — changes on port re-plug (Windows) |
| `SerialNumber` | ❌ No — many devices omit it | ❌ No — cheap hardware often shares serials | ✅ Yes |

`DeviceInfo.Id` is chosen because it is always present and always unique within a session. Port-instability is acceptable: if a device is re-plugged into a different port, the old latch is cleared by the `Disconnected`/`Disappeared` cascade, and the new arrival (with a new Id) re-establishes the latch. This is the correct behaviour — a re-plug is a new connection.

**Why soft (not hard) latch:**

A hard latch (persist after disconnect, waiting for the same Id) would prevent any other device from claiming the profile after the first device disconnects. This is rarely desirable and creates a difficult-to-diagnose state where `Device` remains permanently null even when a valid device is connected. Soft latching (clear on disconnect) is the natural behaviour: "I had a device, it left, the next matching one can take its place."

**Interaction with `IsAmbiguous`:**

With the latch in place, `_connectedByProfile[profile].Count > 1` cannot occur in normal operation — the second device's arrival is rejected at the latch check. `IsAmbiguous` therefore serves as a safety net for the simultaneous-arrival race condition: if two events are serialised through `_lock` in rapid succession, the first event establishes the latch and the second is rejected before `Resolve()` is called. In practice `IsAmbiguous` should never be observed; it remains in the API as a documented invariant guard.

**Thread safety:** The latch check, latch assignment, and list insertion all happen inside `_lock` in a single critical section. There is no window between "check latch" and "set latch" where a concurrent thread could insert a second device.

---

## Proposed Public API Surface

```csharp
// ── New type ───────────────────────────────────────────────────────────

public sealed class DeviceProfile
{
    public DeviceProfile(Action<DeviceFilter> configure, string? name = null)
    public string? Name { get; }
}

// ── DeviceTracker (updated) ────────────────────────────────────────────

public sealed class DeviceTracker : INotifyPropertyChanged, IObservable<bool>
{
    // Single-profile convenience — existing callsites compile unmodified
    public DeviceTracker(Action<DeviceFilter> configure, string? name = null)

    // Multi-profile — ordered highest-priority first
    public DeviceTracker(string? name, params DeviceProfile[] profiles)

    public string? Name { get; }

    // ── Resolved state ─────────────────────────────────────────────────
    public DeviceInfo? Device { get; }           // highest-priority unambiguous connected device
    public DeviceInfo? PresentDevice { get; }    // highest-priority unambiguous present device
    public bool IsConnected { get; }             // Device != null
    public bool IsPresent { get; }               // PresentDevice != null
    public DeviceProfile? ActiveProfile { get; } // profile that resolved or collided (null if nothing matched)

    // ── Ambiguity ───────────────────────────────────────────────────────
    public bool IsAmbiguous { get; }
    public IReadOnlyList<DeviceInfo> AmbiguousDevices { get; }

    // ── Notifications (contract unchanged) ─────────────────────────────
    public event EventHandler? StateChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IDisposable Subscribe(IObserver<bool> observer);
}
```

**Single-profile usage — existing feel, new property:**

```csharp
// Before
var mouse = watcher.Track(t => t.WithUsbId("046D", "C52B"), name: "Mouse");
if (mouse.ConnectedDevices.Count > 0)
    Use(mouse.ConnectedDevices[0]);

// After
var mouse = watcher.Track(t => t.WithUsbId("046D", "C52B"), name: "Mouse");
if (mouse.Device is { } d)
    Use(d);  // confident: exactly one match, or null
```

**Multi-profile fallback:**

```csharp
var mouse = new DeviceTracker("Mouse",
    new DeviceProfile(f => f.WithUsbId("046D", "C52B"), name: "MX Master"),
    new DeviceProfile(f => f.WithUsbId("046D", "C534"), name: "M705"),
    new DeviceProfile(f => f.WithName("USB Input Device"), name: "Dev HID"));

await using var watcher = Devices.Watch().Track(mouse);
await watcher.StartAsync();

mouse.StateChanged += (_, _) =>
{
    if (mouse.IsAmbiguous)
        Warn($"Conflict on '{mouse.ActiveProfile!.Name}': {mouse.AmbiguousDevices.Count} devices connected.");
    else if (mouse.Device is { } d)
        Use(d, profileLabel: mouse.ActiveProfile!.Name);
};
```

**Ambiguity scenario:**

```csharp
// Two MX Masters plugged in simultaneously:
// mouse.IsAmbiguous         = true
// mouse.Device              = null
// mouse.ActiveProfile.Name  = "MX Master"
// mouse.AmbiguousDevices    = [DeviceInfo("USB\\VID_046D...\\1"), DeviceInfo("USB\\VID_046D...\\2")]
// mouse.IsConnected         = false  (Device == null)

// User unplugs one:
// mouse.IsAmbiguous         = false
// mouse.Device              = DeviceInfo("USB\\VID_046D...\\2")
// mouse.IsConnected         = true
// → StateChanged fires, IObservable<bool> pushes true
```

---

## Consequences

### Positive

- **Single confident reference** — `tracker.Device` is null or one `DeviceInfo`. No `Count` check, no "which element?" decision at the call site.
- **Expressive fallback chains** — primary / compatible / dev-board priority is fully described in one tracker, with one `StateChanged` subscription.
- **Structured conflict signal** — `IsAmbiguous` + `AmbiguousDevices` is actionable in production: XAML-bindable for a warning indicator, loggable for diagnostics, automatically resolves when a device is unplugged.
- **Zero provider changes** — `IDeviceProvider`, `IDeviceMonitorProvider`, `WindowsDeviceProvider`, and `WindowsDeviceMonitorProvider` are all untouched.
- **Zero watcher fan-out changes** — `DeviceWatcher.FanOut*` methods are identical; the resolution logic is entirely internal to `DeviceTracker`.

### Negative / Risks

- **Breaking change on `PresentDevices` / `ConnectedDevices`** — any external consumer of these properties will not compile after the change. Mitigation: the change is deliberate and communicated via a major-version increment (or pre-1.0 semver policy).
- **`Resolve()` runs on every state update** — O(N×M) where N = profile count and M = devices per profile. In practice N ≤ 5 and M ≤ 3; the cost is negligible. If pathological cases emerge, a dirty-flag optimization defers resolution until a property is read.
- **Ambiguity stops fallthrough** — a conflict at profile[0] does not activate profile[1]. This is intentional but may surprise consumers who expect "any match is fine." Those consumers should use the single-profile constructor and accept `IsConnected = true` when any device matches.
- **`ActiveProfile` is null when nothing matches** — requires a null check before reading `ActiveProfile.Name`. Callers who only care about `IsConnected` are unaffected.

---

## Open Questions

1. **`DeviceWatcher.Track` multi-profile overload?** A convenience overload `Track(string? name, params DeviceProfile[])` returning `DeviceTracker` would avoid `new DeviceTracker(...)` for the watcher-factory creation path. Low priority — `Track(DeviceTracker)` already composes cleanly with the direct constructor.

2. **Per-profile `AmbiguityBehavior`?** An enum (`Stop`, `FallThrough`) on `DeviceProfile` would let callers configure whether ambiguity at a profile causes it to stop or fall through to the next. Deferred — `Stop` is correct for all known use cases, and the complexity is not justified without a concrete demand.

3. **`AmbiguousDevices` for the present-state path?** Symmetric ambiguity machinery (`IsPresentAmbiguous`, `AmbiguousPresentDevices`) could be added. Deferred — the connected path is the primary actionable signal; present-state conflict is less urgent and can be added without breaking changes.

4. **`DeviceProfile` as a record?** `DeviceProfile` is a sealed class today. Switching to `record` would add value equality (two profiles with the same filter and name are equal). Not currently needed — profiles are compared by reference as dictionary keys. Revisit if serialization or config-binding scenarios emerge.

---

## Impact on Existing Types

| Type | Change | Scope |
|---|---|---|
| `DeviceTracker` | Major internal restructure. Replace `_filter` + two flat lists with `_profiles` + two `Dictionary<DeviceProfile, List<DeviceInfo>>` + two latch dictionaries (`_presentLatch`, `_connectedLatch`). Add `ResolveConnected()`, `ResolvePresent()`, `CaptureState()`, `NotifyChanges()`. Replace `PresentDevices`/`ConnectedDevices` with `PresentDevice?`/`Device?`. Add `ActiveProfile`, `IsAmbiguous`, `AmbiguousDevices`. Update `Matches()`. | `Periphery/DeviceTracker.cs` |
| `DeviceProfile` | **New type.** Wraps `DeviceFilter` + `Name`. Internal constructor accepts `DeviceFilter` directly for use by `DeviceWatcher.Track`. | `Periphery/DeviceProfile.cs` |
| `DeviceWatcher` | No logic changes. `Track(Action<DeviceFilter>, string?)` factory is unchanged. Fan-out methods are unchanged. | `Periphery/DeviceWatcher.cs` |
| `DeviceFilter` | None. | — |
| `IDeviceProvider` / `IDeviceMonitorProvider` | None. | — |
| `WindowsDeviceProvider` / `WindowsDeviceMonitorProvider` | None. | — |
| `DeviceTrackerTests` | All tests referencing `ConnectedDevices`, `PresentDevices` must be rewritten. Multi-device tests become ambiguity tests. New test groups: profile resolution ordering, fallback activation, ambiguity entry/exit, `ActiveProfile` transitions. | `Periphery.Tests/DeviceTrackerTests.cs` |
| `Program.cs` (examples 12–13) | Replace `ConnectedDevices.Count` / `PresentDevices` references. Add multi-profile example. | `Periphery.Examples/Program.cs` |
| `docs/ARCHITECTURE.md` | Update §2.2 — `DeviceTracker` description, `Device`/`PresentDevice`/`IsAmbiguous`, example code. | `docs/ARCHITECTURE.md` |
| Contributor guide | Update type hierarchy section — replace `PresentDevices`/`ConnectedDevices` with `Device`/`PresentDevice`/`IsAmbiguous`/`ActiveProfile`. | `README.md` (Contributing) |

---

## References

- `docs/adr/0001-device-tracking-handles.md` — Original tracking design; §3 (set-tracking model) and §6 (API shape) are superseded by this ADR.
- `docs/adr/0004-two-level-device-state-model.md` — `IsPresent`/`IsConnected` orthogonality is preserved; this ADR does not change their semantics, only how they are derived.
- `Periphery/DeviceTracker.cs` — Implementation target
- `Periphery/DeviceWatcher.cs` — Fan-out logic (unchanged)
- `Periphery/DeviceFilter.cs` — Filter model (reused by `DeviceProfile`)
- `Periphery.Tests/DeviceTrackerTests.cs` — Tests to be rewritten
- `Periphery.Examples/Program.cs` — Examples to be updated
