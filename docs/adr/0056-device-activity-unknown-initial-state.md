---
title: "ADR-0056: `DeviceActivityStatus.Unknown` — a first-class \"not yet enumerated\" initial state"
status: "Accepted"
date: "2026-06-11"
authors: "@charles8051 (design)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0056: `DeviceActivityStatus.Unknown` — a first-class "not yet enumerated" initial state

**Tracks:** `DeviceActivityStatus`, `DeviceTracker`, `DeviceTrackerState`, `DeviceWatcher`; (cross-repo) the kiosk consumer's `DeviceTrackingService`, Prognosis `HealthStatus`
**Related:** ADR-0004 (two-level present/active state model), ADR-0029 (DeviceTracker edge events), ADR-0046 (runtime tracker reconfigure), ADR-0049 (cooperative observers), ADR-0055 (injectable reconnect policy / `ConnectionState`)

> **Number provisional.** Per this repo's convention the ADR number is assigned at merge; renumber if `0056` is taken by a parallel branch.

> **Stance note.** Periphery's stated stance is that it has "no external consumers, breaking changes are fine." That is the *publishing* posture and it still holds — there is no public-stability commitment. This ADR nonetheless treats the change as a **contract change with real consumers** (the kiosk consumer binds against the published `Periphery` package; the fleet consumer's shared library references the package version line, see Backward compatibility) and reasons through their migration explicitly, because the value being changed — *the first value every `IObservable<DeviceTrackerState>` subscriber sees* — is load-bearing on the kiosk health gate. "Breaking changes are fine" means we don't ship a compat shim; it does **not** mean we skip the impact analysis.

---

## Context

### The conflation, at the byte level

`DeviceActivityStatus` (`src/Periphery/DeviceActivityStatus.cs:6-22`) is a three-value enum with **no explicit backing values**, so C# assigns them ordinally:

```csharp
public enum DeviceActivityStatus
{
    Absent,   // = 0
    Present,  // = 1
    Active,   // = 2
}
```

`DeviceTracker._activityStatus` is a plain field (`src/Periphery/DeviceTracker.cs:62`) and is **never initialised in any constructor** — the constructors call only `InitProfileDictionaries()` (`DeviceTracker.cs:106, 141, 148`). A freshly-constructed, not-yet-bound, not-yet-enumerated tracker therefore reports `_activityStatus == default(DeviceActivityStatus) == Absent`.

That value is **byte-for-byte indistinguishable** from the value `Resolve()` writes when the watcher has fully enumerated the OS device tree and found nothing matching (`DeviceTracker.cs:622-624`):

```csharp
_device = null;
_activityStatus = DeviceActivityStatus.Absent;   // "enumerated, confirmed gone"
_activeProfile = null;
```

So `Absent` means **two different things** that a consumer cannot tell apart:

| Situation | How the tracker reaches it | What it *means* |
|---|---|---|
| Just constructed; watcher not started, or started but its initial enumeration has not reached completion | the uninitialised field default | "I have not looked yet." |
| Watcher enumerated the whole tree; nothing matched this tracker's filter | `Resolve()` fall-through | "I looked. It is genuinely not here." |

There is **no per-tracker "initial determination complete" signal** anywhere in the library. I grepped `src/Periphery` for every plausible name (`SnapshotComplete`, `EnumerationComplete`, `InitialSnapshot`, `settled`, …) and found nothing. The watcher *knows* the moment — `DeviceWatcher.SnapshotCurrentDevicesAsync` returns after its `await foreach` drains (`DeviceWatcher.cs:731-769`) and `StartAsync` logs "Device watcher started" (`DeviceWatcher.cs:702`) — but it never calls back into the trackers to say "the first pass is done."

### The consumer false-trip

The first and (today) primary consumer is the kiosk's `DeviceTrackingService` (the kiosk consumer's `Services/DeviceTracking/DeviceTrackingService.cs`). It wires nine device-connection health probes as a hard binary on tracker activity, **in its constructor** (i.e. at DI resolution, long before `StartAsync`):

```csharp
// DeviceTrackingService.cs:127-128
probe.ReplaceHealthProbe(() =>
    tracker.IsActive ? HealthStatus.Healthy : HealthStatus.Unhealthy);
```

The watcher is not started until `StartAsync` (`DeviceTrackingService.cs:211-215`), so at wire-up time **every tracker has `IsActive == false`** and all nine probes read `Unhealthy`. The service's own comment already documents this as a known wart (`DeviceTrackingService.cs:84-96`): "*…which is always Unhealthy because the DeviceWatcher doesn't start until StartAsync runs later.*"

The kiosk serviceability gate then treats a `Required` `Unhealthy` leaf as `OutOfService` (traced in detail in the kiosk consumer's `docs/explorations/transient-startup-health-warmup.md` §2). Net effect, root-caused on `the field unit` 2026-06-08: the kiosk **flashes `OutOfService` for several seconds on every cold boot** for subsystems that are actually fine but have not enumerated yet, and the fleet logs a real `KioskUnhealthy` heartbeat in that window. The battery connection probe tripped `Unhealthy` ~13s before the first successful battery poll — charge was 100% the whole time.

The exploration doc names the general shape **"the unknown-as-failed conflation"** (§3): a verdict is reported for a state that has not been measured, because the type has no "not yet measured" value the consumer is forced to handle, so a default sentinel silently stands in for "no data" and a downstream decision is taken on it. That doc reasons about the *health* layer; this ADR fixes the same conflation one layer **upstream**, at its source: the tracker's `ActivityStatus`.

### Why this is Periphery's to fix, not the consumer's

The kiosk *could* re-derive "have we enumerated yet" itself — the exploration's Direction 3 sketches exactly that: "*gate the resolution on watcher started and this tracker has seen its first `StateChanged`/enumeration, not merely on `IsActive`.*" But:

- **The fact is Periphery's.** Periphery owns enumeration. It alone knows when the initial snapshot has settled per tracker. A consumer can only *approximate* the signal — e.g. "watcher.StartAsync returned" is process-global, not per-tracker, and races the `Task.Run` offload (`DeviceWatcher.cs:675-700`); "saw a first `StateChanged`" never arrives for a genuinely-absent device, because an unmatched tracker that stays `Absent` fires **no** `StateChanged` at all (the snapshot fan-out never touches it).
- **Every consumer would re-derive it, fragilely and differently.** The kiosk has nine probes; a future fleet or frame-flow consumer would reinvent the same warmup heuristic. The conflation is intrinsic to the tracker contract, so the fix belongs in the contract.
- **The symmetry is already chosen elsewhere.** Prognosis models "not yet probed" as a first-class `HealthStatus.Unknown` *value* (`repos/prognosis/HealthStatus.cs:13`), deliberately **not** as a parallel `HasBeenEvaluated` flag bolted onto a binary. The exploration doc's recommendation (§5) and the Prognosis design both land on "model not-yet-determined as a status value, never a side-channel boolean." Periphery should mirror that so the two libraries compose by a clean total mapping (`DeviceActivityStatus.Unknown → HealthStatus.Unknown`) instead of a warmup heuristic glued across the seam.

### Precedent inside Periphery

This is not a novel shape for the codebase:

- `DeviceStatus` already uses `Unknown = 0` for "status could not be determined" (`src/Periphery/DeviceStatus.cs:18`).
- `BusType` uses `Unknown` as "the safe default" (`docs/ARCHITECTURE.md:898`).
- `ARCHITECTURE.md:714` even pre-floats a future `ConnectionConfidence { Definitive, Heuristic, Unknown }` "if consumer demand warrants it."

`DeviceActivityStatus` is the one lifecycle enum in the family that lacks an "I don't know yet" value, and that gap is precisely what bites.

---

## Decision

**1. Add `DeviceActivityStatus.Unknown` as the initial, lowest state.**

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<DeviceActivityStatus>))]
public enum DeviceActivityStatus
{
    /// <summary>
    /// The tracker has not yet determined the device's status: it is unbound,
    /// or bound to a watcher whose initial enumeration has not yet completed.
    /// This is the value a tracker reports before its first determination — it
    /// is distinct from <see cref="Absent"/> ("enumerated and confirmed gone").
    /// A tracker leaves Unknown exactly once: when the watcher's initial
    /// enumeration settles (or when a matching device is observed first).
    /// </summary>
    Unknown = 0,

    /// <summary>No matching device is known to the OS (enumerated and confirmed gone).</summary>
    Absent = 1,

    /// <summary>A matching device is known to the OS but not currently active.</summary>
    Present = 2,

    /// <summary>A matching device is active and ready to use.</summary>
    Active = 3,
}
```

`Unknown` takes ordinal `0` so it is the field default — a tracker is `Unknown` from construction with **zero** constructor changes, and we never have to remember to seed it. The other three members gain **explicit** backing values so the renumber (`Absent` 0→1, `Present` 1→2, `Active` 2→3) is deliberate and visible rather than an accident of insertion order.

**1a. Serialize by name** (folded in from the former open question OQ-1, now decided). The enum carries `[JsonConverter(typeof(JsonStringEnumConverter<DeviceActivityStatus>))]`, matching the existing `DeviceStatus` precedent (`src/Periphery/DeviceStatus.cs:14`). Without it, System.Text.Json serializes the enum by **integer**, and the `Unknown = 0` renumber (`Absent` 0→1, `Present` 1→2, `Active` 2→3) would silently shift every persisted/transmitted integer. Serialize-by-name makes the wire form the stable member *name*, so the renumber is invisible to any serializer and no consumer can come to depend on the integer. This is safe to land in the same commit: the audit below found **no integer persistence** of `ActivityStatus` in any consumer (the kiosk DTOs that carry it are in-memory and rebuilt each run) and **no ordinal comparison** of the enum in any consumer (only `== Active` equality, `.ToString()`, and the derived `IsActive`/`IsPresent` bools), so the renumber is ordering-safe and the converter closes the one remaining wire-format hazard.

**2. The watcher drives `Unknown → determined` at initial-enumeration-complete.**

A matched tracker resolves naturally during the snapshot fan-out (a real `OnDeviceAppeared`/`OnDeviceConnected` call runs `Resolve()` and leaves `Unknown`). An **unmatched** tracker receives no fan-out call and would otherwise sit `Unknown` forever. To close that, `DeviceWatcher` signals "initial snapshot processed" to every bound tracker exactly once, at the end of `SnapshotCurrentDevicesAsync`; each still-`Unknown` tracker transitions to its `Resolve()`d determined state — `Absent` for the genuinely-absent case. The design of that signal is the subject of the next section (it is the load-bearing part of this ADR).

This is the whole behavioural change. `IsActive` (`== Active`), `IsPresent` (`Device is not null`), the latch logic, `Resolve()`, and every edge event are otherwise untouched.

---

## The reactive / observable contract (the heart of this ADR)

This section resolves the five subtleties the change turns on. Each is grounded in the current source.

### Subtlety 1 — `IObservable<DeviceTrackerState>`: the first emitted value

`Subscribe` replays the **current** captured state synchronously to the new observer (`DeviceTracker.cs:274-293`):

```csharp
lock (_lock) { _observers.Add(observer); current = CaptureState(); }
observer.OnNext(current);
```

`CaptureState()` reads `_activityStatus` (`DeviceTracker.cs:628-629`). So a subscriber that attaches to a *fresh* tracker today receives a first value of `Absent`; **after this change it receives `Unknown`.**

What every existing subscriber sees change:

- **The kiosk consumer's log subscriber** (`DeviceTrackingService.cs:143-153`): `tracker.Select(x => x.IsActive).DistinctUntilChanged()`. `IsActive` is `false` for both `Unknown` and `Absent`, so the *projected* first value is unchanged (`false`). No behavioural difference. ✔
- **The kiosk consumer's health probes** (`DeviceTrackingService.cs:127-135`): these subscribe to `StateChanged` (not the observable) and read `tracker.IsActive` on demand; the first-replay value does not flow here. The fix to *these* is the consumer-composition change below (map `Unknown → HealthStatus.Unknown`), not an automatic consequence of the first-value change. ✔ (no silent break; an explicit, intended change)
- **`DeviceProxyBase`** (`src/Periphery/DeviceProxyBase.cs`): keys entirely on `_tracker.IsActive` + `_tracker.Device` (`DeviceProxyBase.cs:102, 252-254, 423`). Both are `false`/`null` under `Unknown` exactly as under `Absent`, so the proxy/session-host cohort (camera, mechanism, barcode, HID, and `DeviceSessionHost`/`MultiDeviceSessionHost` on top) is **naturally `Unknown`-safe** and opens nothing spuriously. ✔
- **`MultiDeviceTracker`** (`src/Periphery/MultiDeviceTracker.cs`): its observable replays each *child's* `CurrentState` (`MultiDeviceTracker.cs:113-120`). Children are created only on first appearance (`GetOrCreateChild`, `MultiDeviceTracker.cs:201-226`), so a child is born already-matched and never sits at `Unknown`. A group subscriber therefore never observes a child `Unknown` via the normal path. (See the lifecycle matrix for the deliberate choice *not* to seed children `Unknown`.) ✔
- **Direct `.ToString()` consumers** (a tracker view-model, a device-host service, `examples/scripts/rx-demo.cs:28`): render the enum name; they will display `"Unknown"` during warmup. Cosmetic and arguably an improvement. ✔

**No existing subscriber relies on the initial value being `Absent` for correctness.** The closest is the kiosk health probe, and changing *that* mapping is the explicit point of the exercise.

### Subtlety 2 — BehaviorSubject-like replay to LATE subscribers

The doc comment on `Subscribe` says the tracker "behave[s] like a BehaviorSubject" (`DeviceTracker.cs:266-267`); ADR-0029 repeats it (line 20). The correctness question is: does a subscriber attaching **after** enumeration replay the *current determined* state, or a stale initial `Unknown`?

**It replays the current value, and this is already correct — no change required.** The replay path captures `CaptureState()` *at subscribe time* under `_lock` (`DeviceTracker.cs:277-282`), not a stored initial snapshot. `_activityStatus` has, by then, been overwritten by `Resolve()` (during fan-out) or by the enumeration-complete signal. The existing test pins this exactly: `Subscribe_LateSubscriber_ReceivesCurrentState` connects after `OnDeviceConnected` and asserts the single replayed value has `IsActive == true` (`tests/Periphery.Tests/Tracker/DeviceTrackerTests.cs:578-590`).

So a late subscriber **never** sees a spurious `Unknown`: by the time it subscribes, the tracker has left `Unknown`. `Unknown` is only ever observed by a subscriber that was *already attached* during the warmup window, which is precisely who should see the `Unknown → determined` transition. This is the BehaviorSubject semantic working as intended; the new initial value rides it for free.

(One honest caveat, unchanged from today: a subscriber that attaches **between** construction and enumeration-complete — e.g. the kiosk constructs trackers and subscribes in its own constructor, then calls `StartAsync` later — *will* legitimately receive `Unknown` first, then the determined value. That is the entire point and the desired behaviour, not a defect.)

### Subtlety 3 — the transition sequence and emission count

`NotifyChanges` fires `StateChanged` + observer `OnNext` iff `before` and `after` differ on `Device`, `ActivityStatus`, or `ActiveProfile` (`DeviceTracker.cs:631-665`). The transition out of `Unknown` flips `ActivityStatus`, so it always produces **exactly one** emission. There is no intermediate flicker because `Resolve()` computes the final determined value in a single locked pass (`DeviceTracker.cs:595-625`) before `after` is captured.

Canonical sequences (each arrow = one `StateChanged` emission):

| Scenario | Sequence |
|---|---|
| USB device present at startup (matched) | `Unknown → Active` (single emission; `Appeared`+`Activated` fire together — see Subtlety 5) |
| Bluetooth paired-but-out-of-range at startup | `Unknown → Present` |
| No matching device at startup (unmatched) | `Unknown → Absent` (driven by the enumeration-complete signal) |
| Hot-plug after a settled `Absent` | `Absent → Present`/`Active` (unchanged from today) |
| Device present at startup, later unplugged | `Unknown → Active → Absent` |

The only genuinely *new* emission in the system is `Unknown → Absent` for unmatched trackers. Matched trackers previously emitted `Absent → Active` (the default `Absent`, then the fan-out); they now emit `Unknown → Active` — same count, different first value.

### Subtlety 4 — the `Unknown → determined` driver (load-bearing design)

A matched tracker leaves `Unknown` on its own (the fan-out delivers a real device-event that runs `Resolve()`). The problem is the **unmatched** tracker: nothing in the current code touches it after construction, so under a naive `Unknown` change it would stay `Unknown` forever. The watcher must signal initial-enumeration-complete per tracker.

**Chosen design: a one-shot internal fan-out hook, fired after the snapshot drains.**

1. Add an internal tracker method:

   ```csharp
   // DeviceTracker — called once per watcher start, after the initial snapshot settles.
   internal void OnInitialEnumerationComplete()
   {
       DeviceTrackerState before, after;
       lock (_lock)
       {
           if (_activityStatus != DeviceActivityStatus.Unknown) return; // already determined by fan-out
           before = CaptureState();
           Resolve();              // Unknown -> Absent (latches are empty for an unmatched tracker)
           after = CaptureState();
       }
       NotifyChanges(before, after);
   }
   ```

   For an unmatched tracker the latches are empty, so `Resolve()` falls through to `Absent` (`DeviceTracker.cs:622-624`) and `NotifyChanges` emits the single `Unknown → Absent`. For a tracker the fan-out already resolved, the early-return makes the call a no-op (no spurious re-emit) — this guards the race where a matched tracker's status is set during the same snapshot.

2. `DeviceWatcher` fans the hook out once, at the end of `SnapshotCurrentDevicesAsync` (after the `await foreach`, around `DeviceWatcher.cs:768`):

   ```csharp
   foreach (var tracker in _trackers)
       tracker.OnInitialEnumerationComplete();
   // (MultiDeviceTracker needs no call — its children are born matched; see matrix.)
   ```

   This runs inside the `Task.Run` offload (`DeviceWatcher.cs:675-700`), on the same thread-pool thread that fired the snapshot's `Appeared`/`Activated` fan-out, *before* `StartAsync` returns. So the post-condition "`StartAsync` completed ⇒ no bound tracker is `Unknown`" holds for callers that await it.

**Why this shape** (vs. alternatives weighed in the Alternatives section): it reuses the existing `Resolve()` + `NotifyChanges` path verbatim, adds no new public surface, fires the minimum emissions (one per still-`Unknown` tracker, zero for matched ones), and slots into the one place the watcher already owns the "snapshot is done" moment. It mirrors the `Unbind()` shape (`DeviceTracker.cs:457-475`) — capture before, mutate under lock, notify once.

Lifecycle cases (full matrix in the next section; the load-bearing ones):

- **(a) bound before `StartAsync`:** resolves at enumeration-complete via the hook. Sequence `Unknown → Absent` (unmatched) or `Unknown → Active`/`Present` (matched, via fan-out before the hook no-ops).
- **(b) added after start via `ReplayKnownDevicesTo` (reconfigure, ADR-0046):** the watcher's `_deviceCache` is already settled, so `ApplyProfiles` replays it synchronously under `_lock` and calls `Resolve()` (`DeviceTracker.cs:380-402`). **The tracker resolves directly to its determined state in that locked block — it never publishes `Unknown`.** Because `Reconfigure`/`ReplaceProfiles` re-run `InitProfileDictionaries()` (`DeviceTracker.cs:389`) which does *not* touch `_activityStatus`, a reconfigure of an already-determined tracker starts from its prior status, not `Unknown`; and even a reconfigure that happens to land on "no match" goes `previous → Absent` in one batched `NotifyChanges` (`DeviceTracker.cs:401`), never through `Unknown`. So late-added/reconfigured trackers **skip `Unknown` entirely** — correct, since the snapshot they bind against is already determined. (The watcher does **not** call the enumeration-complete hook on a reconfigure; the hook is start-scoped.)
- **(c) watcher never starts:** the tracker stays `Unknown` indefinitely. This is **correct and intentional** — nothing has enumerated, so "unknown" is the truthful state. (Contrast today's misleading `Absent`.) A consumer that wants a deadline imposes it in its own shell — e.g. the kiosk's warmup backstop in the exploration doc §5c; Periphery does not invent a timer (functional-core / no-clock-in-core preference). Disposing a never-started watcher still drives `Unknown → Absent` via `Unbind()` (see dispose/restart below), so a tracker does not get *stranded* `Unknown` across a watcher's lifetime — only while a live-but-unstarted watcher is held.
- **(d) runtime re-enumeration / reconfigure (ADR-0046):** a reconfigure does **not** reset a determined tracker to `Unknown` (see (b)). This is the right call: the watcher's cache is still warm, so re-evaluation is immediate and a transient `Unknown` would be a lie (we *do* know — we just changed the filter). `Unknown` means "never determined since bind," not "determined, then filter changed."

### Subtlety 5 — event semantics across the `Unknown` boundary

The four edge events fire purely on `IsPresent`/`IsActive` deltas (`DeviceTracker.cs:657-660`, canonicalised in ADR-0029:175-178). Crucially, **`Unknown`, like `Absent`, has `IsPresent == false` and `IsActive == false`** (`Device` is null, `ActivityStatus != Active`). So the edge-event predicates behave *identically* whether the "before" state is `Unknown` or `Absent`:

| Transition | `before.IsPresent → after` | `before.IsActive → after` | Events fired |
|---|---|---|---|
| `Unknown → Active` | `false → true` | `false → true` | **`Appeared` + `Activated`** |
| `Unknown → Present` | `false → true` | `false → false` | **`Appeared`** only |
| `Unknown → Absent` | `false → false` | `false → false` | **none** |

This is exactly right:

- `Unknown → Active` (USB device present at boot) fires `Appeared` + `Activated` — the device *did* just become known-and-active from this tracker's perspective. Identical to today's `Absent → Active`.
- `Unknown → Absent` (unmatched tracker resolving at enumeration-complete) fires **nothing** — the device was never present, so there is no edge. The state-level `StateChanged`/`OnNext` still fires (status changed `Unknown → Absent`), but no `Appeared`/`Disappeared`/`Activated`/`Deactivated`. **This is the desired semantic:** a tracker discovering "my device isn't here" must not masquerade as a `Disappeared` (nothing appeared, so nothing disappeared).

**Nothing in the edge-event logic changes.** The predicates already produce the correct firing because `Unknown` shares the `(IsPresent=false, IsActive=false)` projection with `Absent`. The only observable difference is the *first* `StateChanged`/`OnNext` carrying `Unknown` instead of `Absent`. I verified there is no edge predicate keyed on `ActivityStatus` equality (only on the two derived bools) — so renaming the default from `Absent` to `Unknown` cannot alter which edges fire.

---

## Edge cases / lifecycle matrix

| # | Lifecycle | First status | Driver of resolution | Sequence | Edge events |
|---|---|---|---|---|---|
| a | Bound before `StartAsync`, device present | `Unknown` | snapshot fan-out (`OnDeviceConnected`) | `Unknown → Active` | Appeared+Activated |
| a' | Bound before `StartAsync`, BT paired/out-of-range | `Unknown` | snapshot fan-out (`OnDeviceAppeared`) | `Unknown → Present` | Appeared |
| a'' | Bound before `StartAsync`, no match | `Unknown` | `OnInitialEnumerationComplete` hook | `Unknown → Absent` | none |
| b | Added after start via reconfigure, cache has match | (prior) | `ApplyProfiles` → `Resolve()` under lock | `→ Active/Present` (skips `Unknown`) | per net delta |
| b' | Added after start via reconfigure, cache no match | (prior) | `ApplyProfiles` → `Resolve()` under lock | `→ Absent` (skips `Unknown`) | none |
| c | Watcher never started, tracker held | `Unknown` | none (correct) | stays `Unknown` until disposed/bound | none |
| d | Re-enumerate / `Reconfigure` on a determined tracker | (prior) | `ApplyProfiles` | single batched delta, no `Unknown` | per net delta (ADR-0046 §5) |
| e | `Unbind()` (watcher dispose / stop) | (prior) | `Unbind` resets to `Absent` | `prior → Absent` | Disappeared/Deactivated if was present/active |
| f | Re-`Bind()` to a new watcher, then `StartAsync` | `Absent` (post-unbind) | new snapshot | `Absent → …` | per delta |

**Dispose / restart (case e/f) — a deliberate asymmetry.** `Unbind()` resets `_activityStatus = DeviceActivityStatus.Absent` (`DeviceTracker.cs:470, 474`), **not** `Unknown`. The ADR keeps it `Absent`, on purpose:

- A tracker that has *been through* a watcher lifecycle and is now detached is better described as `Absent` ("as far as the last enumeration knew, gone / now inert") than `Unknown` ("never looked"). `Unknown` is specifically the *pre-first-determination* state; `Unbind` is post-determination teardown.
- It preserves today's `Unbind` contract byte-for-byte: existing tests (`Unbind_NotifiesObserverWithFalse` asserts the post-unbind observed value has `IsActive == false`, `DeviceTrackerTests.cs:662-674`; `Unbind_ClearsResolvedState`, `:618-632`) keep passing unchanged.
- On **re-bind + re-start**, the tracker therefore starts the *new* lifecycle from `Absent`, not `Unknown`. This is a minor wrinkle (a re-attached tracker doesn't get a fresh `Unknown` warmup window) but acceptable: the consumer that cares about warmup (the kiosk gate) keys off *process* startup, and a mid-life watcher swap is not a cold boot. **Open question OQ-3** asks whether `Unbind` should instead reset to `Unknown` for full symmetry; I recommend `Absent` and flag it for the human.

---

## Backward compatibility & migration

Periphery ships as a NuGet package (`Periphery`, `1.0.0-alpha.*`). The relevant consumers:

- **The kiosk consumer** (via the kiosk consumer's shared library) — the live consumer; binds trackers, reads `IsActive`/`IsPresent`/`ActivityStatus`, subscribes to the observable. Detailed migration below.
- **The fleet consumer's shared library** — references the `Periphery` / `Periphery.Camera` / `Periphery.Hid` package versions in its `Directory.Packages.props:161-166`, co-bumped to keep the alpha line aligned. **It does not currently use `DeviceActivityStatus`, `DeviceTracker`, or `IsActive` in code** (verified by grep — the only `Periphery` hits are the package-version pins and one ADR mention). So the fleet consumer is a *transitive/potential* consumer that takes the new package but is **source-unaffected** by this change today. The prompt's framing of the fleet consumer as an active `DeviceActivityStatus` consumer is, as of this commit, not borne out by its source; I note it as "relevant if/when the fleet consumer grows device-tracking code," not as a site needing migration now.

**What stays correct automatically under `Unknown`:**

- `DeviceTrackerState.IsActive` = `ActivityStatus == Active` (`DeviceTrackerState.cs:15`) → `false` under `Unknown`. ✔
- `DeviceTrackerState.IsPresent` = `Device is not null` (`DeviceTrackerState.cs:18`) → `false` under `Unknown` (no device resolved). ✔
- `DeviceTracker.IsActive`/`IsPresent` (`DeviceTracker.cs:200-212`) → both `false`. ✔
- `DeviceProxyBase` and the session-host cohort (key on `IsActive`/`Device`) → no spurious opens. ✔

**What changes and needs a consumer touch:**

1. **Exhaustive `switch` on `ActivityStatus`.** Any `switch` *expression* over `DeviceActivityStatus` with no discard arm now needs an `Unknown` case (or a default) to stay exhaustive / non-warning. **Audit result: there are zero such switches in any consumer today.** Every current consumer uses `.ToString()`, `IsActive`, `IsPresent`, or `== Active` equality (`DeviceTrackerCardViewModel.cs:134`, a device-host service, `BatteryStateSnapshot.cs:31-33`, `BatteryHealthEvaluator.cs:11`). So this is a *latent* compatibility note for future code, not a present break. It still belongs in the ADR because it is the classic enum-addition footgun.

2. **The first emitted observable value** changes `Absent → Unknown` (Subtlety 1). The only consumer reading the raw first value (vs. a projection) is incidental display; the kiosk's `IsActive`-projected subscriber is unaffected.

3. **Integer serialization of the enum.** `DeviceActivityStatus` has **no** `[JsonConverter]` (unlike `DeviceStatus`/`BusType`), so System.Text.Json serializes it by **integer**. Adding `Unknown = 0` and renumbering `Absent`/`Present`/`Active` to `1/2/3` shifts every persisted/transmitted integer. If any consumer persists or wire-transmits `ActivityStatus` as a number, that data's meaning shifts. **Audit: no persistence of `ActivityStatus` integers found** — the kiosk DTOs that carry it (`BatteryStateSnapshot`) are in-memory only and rebuilt each run. Still, this is the sharpest hazard in the change → **Open question OQ-1** (recommend adding a `[JsonConverter(typeof(JsonStringEnumConverter<DeviceActivityStatus>))]` in the same commit, matching `DeviceStatus`, so the wire form is the stable *name* and the renumber is invisible to any serializer).

**Migration for the kiosk consumer (the real one):** see Consumer composition below. In one line: change the nine probe lambdas from a binary to a three-way map and delete the construction-time-`Unhealthy` problem at its root.

**Version bump.** Adding an enum member + changing the default observable value + renumbering integer values is a **breaking** change to the contract (SemVer-major in a stabilised package). On the current `1.0.0-alpha.*` line it is just the next alpha; when Periphery stabilises this would be a major bump. Per the repo stance we ship it with **no compat shim** and update consumers in lockstep — but the ADR records it as breaking so the eventual stabilisation changelog is honest.

---

## Consumer composition: `Unknown → HealthStatus.Unknown`

The payoff. Prognosis already has `HealthStatus.Unknown` ranked `Healthy(0) < Unknown(1) < Degraded(2) < Unhealthy(3)` (`repos/prognosis/HealthStatus.cs:12-15`), and its rollup is worst-wins, so an `Unknown` leaf under a `Required` edge raises its parent **at most to `Unknown`, never `Unhealthy`** (exploration doc §3). That is exactly the non-gating-during-warmup property the kiosk needs.

With `DeviceActivityStatus.Unknown` existing upstream, the kiosk's nine probe lambdas (`DeviceTrackingService.cs:127-128`) become a **clean total mapping** with no warmup heuristic:

```csharp
// after: a total function of the tracker's own status — no "has the watcher started?" guesswork
probe.ReplaceHealthProbe(() => tracker.ActivityStatus switch
{
    DeviceActivityStatus.Active            => HealthStatus.Healthy,
    DeviceActivityStatus.Present           => HealthStatus.Healthy,   // known to OS; openability is the proxy's concern (ADR-0055)
    DeviceActivityStatus.Unknown           => HealthStatus.Unknown,   // not yet enumerated — non-gating during warmup
    DeviceActivityStatus.Absent            => HealthStatus.Unhealthy, // enumerated and genuinely gone — gates, correctly
    _ => HealthStatus.Unhealthy,
});
```

This **removes the entire warmup-window problem the exploration doc was working around** for the *connection* probes:

- During warmup the probe reads `Unknown → HealthStatus.Unknown`, which (by ranking) cannot drive the root `Unhealthy`, so the gate shows `Initializing`, not `OutOfService` — *without* any per-probe timer, "watcher started?" check, or `HasBeenEvaluated` bit on the kiosk side. Periphery resolving `Unknown → Absent` at enumeration-complete is what later *arms* the genuine-failure path: a device that never enumerates resolves to `Absent → Unhealthy` once the snapshot settles, so a real hard-down device still gates.
- The exploration's Directions 1+2+3 collapse substantially: Direction 3 ("device-connection probes start `Unknown`, resolve only once the watcher reports") is now **delivered by the library** instead of re-derived in kiosk wiring; the kiosk keeps only the *policy* half (Direction 1's "`Unknown` is tolerated until warm, gating after," which is a serviceability decision that rightly lives in the consumer, per the authoring guide's one-way rule). The kiosk still needs its warmup-gate policy for the *non-connection* gating leaves (mechanism watchdog, etc.), but the nine connection probes stop being the trigger.

**Deliberate symmetry with Prognosis.** Both libraries now express "not yet determined" as a **first-class status value at the bottom of the lattice**, resolved by the owning subsystem's first real determination — not as a parallel boolean. `DeviceActivityStatus.Unknown` (Periphery owns enumeration) maps 1:1 to `HealthStatus.Unknown` (Prognosis owns health rollup). The seam between them is a total `switch`, not a heuristic. This is the same conclusion the Prognosis side reached ("model not-yet-determined as a status value, never a `HasEnumerated`/`HasBeenEvaluated` side-channel flag"; exploration doc §3, §5a, and the `HealthStatus.Unknown` value itself).

---

## Alternatives considered

- **(A) A parallel `HasEnumerated` / `IsInitialized` bool on `DeviceTracker` (or a `Determined` flag on `DeviceTrackerState`).** Rejected. This is the side-channel-flag anti-pattern the Prognosis design explicitly rejected for the symmetric problem. Every consumer would have to read *two* fields and remember to combine them; the observable would carry a `(status, bool)` pair or fire a separate signal; and `switch`-on-status — the natural consumer shape — could not express "not yet known" at all. A first-class enum value is self-documenting, rides the existing single `DeviceTrackerState` snapshot, composes through `switch`, and maps cleanly to `HealthStatus.Unknown`. The whole point of the change is that "not yet determined" is a *state*, and states belong in the state enum.

- **(B) Keep the distinction consumer-side (the exploration's Direction 3 in kiosk wiring).** Rejected as the *primary* fix. The conflation is intrinsic to the tracker contract (the field default *is* `Absent`), so every consumer re-derives the same warmup heuristic, fragilely: "watcher started" is process-global and races the `Task.Run` offload; "saw first `StateChanged`" never fires for a genuinely-absent device. Periphery owns enumeration and is the only layer that can signal per-tracker initial-determination precisely. Fix it once, at the source. (The kiosk retains the *policy* of how to treat `Unknown` at the gate — that part is correctly consumer-side.)

- **(C) `Unknown` only on the observable / a separate "initial" signal, not on the enum.** Rejected. Splitting the representation (enum says `Absent`, observable says "initializing" via a side path) reintroduces the two-field problem and means `tracker.ActivityStatus` *still* lies during warmup for the many consumers that read the property directly (`DeviceProxyBase`, the debug card, payment host). The enum is the single source of truth for the tracker's resolved status; the fix has to be there.

- **(D) A timer/deadline inside Periphery that auto-resolves `Unknown → Absent` after N seconds even if the watcher never starts.** Rejected. It fuses timing into the tracker's pure state machine, against the functional-core / "state, IO, and timing are separate concerns" preference (and Periphery's own line in ADR-0055: the library owns mechanism, the consumer owns timing policy). The watcher's enumeration-complete signal is the *correct, event-driven* resolver; a never-started watcher legitimately leaves the tracker `Unknown` (truthful), and any consumer wanting a wall-clock backstop adds it in its own shell (kiosk warmup grace, exploration §5c).

- **(E) Make `Absent` itself mean "unknown until proven present" and add a new `Confirmed`/`Gone` value instead.** Rejected. It inverts the established ADR-0004 vocabulary (`Absent` has meant "enumerated, not here" since the two-level model) and would silently change the meaning of `Absent` for every existing consumer and test. Adding `Unknown` at the bottom is additive to the *concept lattice* (even though the integer renumber is a break) and leaves `Absent`'s meaning intact.

- **(F) Reset to `Unknown` on `Unbind` for full symmetry.** Considered, not adopted (see dispose/restart). Recorded as **OQ-3**.

---

## Testing / validation seams

The change is unit-testable end-to-end with the existing fake-provider harness (`DeviceWatcher(IDeviceProvider, IDeviceMonitorProvider)`); no hardware. Required coverage:

**Tracker-level (pure, no watcher):**
- `NewTracker_StatusIsUnknown` — a freshly constructed tracker reports `ActivityStatus == Unknown`, `IsPresent == false`, `IsActive == false`, `Device == null`. (Updates the existing `NewTracker_IsNotPresent_IsNotActive`, `DeviceTrackerTests.cs:30-39`, which currently asserts nothing about the enum; add the `Unknown` assertion.)
- `Subscribe_FirstReplay_IsUnknown` — the first observed value on a fresh tracker is `Unknown`. (Updates `Subscribe_ReceivesTrueOnFirstConnect`/`…OnDisconnect`/`…AppearedOnly`, `DeviceTrackerTests.cs:496-539`, whose comments and assertions currently say "initial replay: Absent" — these become `Unknown`.)
- `OnInitialEnumerationComplete_UnmatchedTracker_ResolvesToAbsent` — call the new hook on a tracker with empty latches; assert `Unknown → Absent` and exactly one `StateChanged`, **no** edge events.
- `OnInitialEnumerationComplete_MatchedTracker_IsNoOp` — after a real `OnDeviceConnected` (status already `Active`), the hook does not re-emit.
- `Unknown_To_Active_FiresAppearedAndActivated` and `Unknown_To_Absent_FiresNoEdgeEvents` — pin the edge-event matrix from Subtlety 5.
- `LateSubscriber_NeverSeesUnknown` — resolve the tracker (or run the hook) first, then subscribe; assert the single replayed value is the determined one, never `Unknown`. (Extends `Subscribe_LateSubscriber_ReceivesCurrentState`, `DeviceTrackerTests.cs:578-590`.)
- `Reconfigure_DeterminedTracker_DoesNotPassThroughUnknown` — reconfigure a resolved tracker; assert no observed value is `Unknown`.
- `Unbind_ResetsToAbsentNotUnknown` — pins the dispose asymmetry (case e).

**Watcher-level (fake providers):**
- `StartAsync_UnmatchedTracker_ResolvesToAbsentExactlyOnce` — bind a tracker whose filter matches nothing in the fake snapshot; after `await StartAsync`, assert `ActivityStatus == Absent` and that the observable saw `Unknown → Absent` (one transition).
- `StartAsync_MatchedTracker_GoesUnknownToActive` — present device in the fake snapshot; assert the observed sequence is `Unknown → Active` with `Appeared`+`Activated`.
- `StartAsync_PostCondition_NoBoundTrackerIsUnknown` — after `await StartAsync`, every bound tracker has left `Unknown`.
- `NeverStarted_TrackerStaysUnknown` — construct + bind, never start; assert `ActivityStatus == Unknown` (case c).
- `ReconfigureAfterStart_SkipsUnknown` — add/reconfigure against a warm cache; assert no `Unknown` is observed (case b/b').

**Consumer-side (the kiosk consumer, follow-up, gated on the Periphery release):**
- The probe `switch` maps `Unknown → HealthStatus.Unknown`; a cold-boot integration test asserts the root is never `Unhealthy` *solely* due to a not-yet-enumerated connection leaf, and that a genuinely-absent device still drives `Unhealthy` once the snapshot settles.

---

## Open questions (for human judgment)

- **OQ-1 (serialization — sharpest). RESOLVED → folded into Decision 1a.** `DeviceActivityStatus` had no `[JsonConverter]`, so it serialized by integer and the renumber (`Absent` 0→1, etc.) would have shifted the wire values. **Decision (this commit):** added `[JsonConverter(typeof(JsonStringEnumConverter<DeviceActivityStatus>))]` (matching `DeviceStatus.cs:14`) so the stable form is the *name* and no consumer can depend on the integer. Verified no integer persistence and no ordinal comparison of the enum in any consumer before landing it. See Decision 1a and the serialization round-trip test pinning the name form.

- **OQ-2 (`MultiDeviceTracker` children).** Children are created already-matched and never sit `Unknown` today (Subtlety 1, matrix). Should a child instead be *born* `Unknown` and resolved in the same breath (cosmetic — it would be `Unknown` for nanoseconds inside `GetOrCreateChild`), or is "children skip `Unknown`" the right contract? **Recommendation:** leave children skipping `Unknown` — a child *only* exists because its device appeared, so it is determined by construction; `Unknown` is meaningless for it. Flagged because it is a contract subtlety a reviewer should bless.

- **OQ-3 (`Unbind` reset value).** Reset to `Absent` (chosen, preserves existing `Unbind` tests and contract) vs. `Unknown` (fuller symmetry — a detached tracker "hasn't been determined by *any current* watcher"). **Recommendation:** `Absent`; `Unknown` is specifically the pre-first-determination state and `Unbind` is post-determination teardown. Low stakes either way; wants a human ruling for the record.

- **OQ-4 (hook timing vs. the `Task.Run` offload).** The enumeration-complete fan-out runs inside `StartAsync`'s `Task.Run` (`DeviceWatcher.cs:675-700`), so the "no bound tracker is `Unknown` after `await StartAsync`" post-condition holds for awaiting callers. But the kiosk does **not** await it — it fires `StartAsync` on a detached continuation (`DeviceTrackingService.cs:218-229`). That is fine (the kiosk *wants* `Unknown` during warmup and resolves it reactively via `StateChanged`), but worth a human confirming the post-condition is "after the returned Task completes," not "synchronously after the call," so no consumer mis-assumes it.

- **OQ-5 (graduate to a numbered cross-cutting decision?).** This ADR is a feature/contract change to the tracker. The *non-gating ranking guarantee* on the Prognosis side (an `Unknown` leaf cannot gate) is a separate, cross-cutting Prognosis property the exploration doc (§9) suggests promoting to a numbered Prognosis ADR. Out of scope here, but the two should cite each other once both land.
