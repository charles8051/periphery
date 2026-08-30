---
title: "ADR-0060: Device reset capability and fault-aware recovery escalation"
status: "Accepted"
status_note: "Shipped - `IDeviceReset`, `ResetStrategy`, `ResetEscalation`, `EscalatingResetRecoveryPolicy`, `IResetSafetyGate`. Extended by [ADR-0075](0075-out-of-band-soft-reset-rung.md)."
date: "2026-06-13"
authors: "@charles8051 (design)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0060: Device reset capability and fault-aware recovery escalation

**Tracks:** `DeviceProxyBase<TDevice,TException>`, `DeviceTracker`, `DeviceWatcher`, `IReconnectPolicy`, `DevNodeHelper`; (cross-repo) a downstream kiosk consumer's `KioskReconnectPolicy` + the Treehopper LED cohort
**Related:** ADR-0055 (injectable reconnect policy — this evolves its contract), ADR-0027 (device-handle base class), ADR-0046 (runtime tracker reconfigure). Three downstream reports motivated it: a `GaveUp` self-heal gap, the EFM8 SPI FIFO wedge ([#93](https://github.com/charles8051/periphery/issues/93)), and a false-healthy session that ADR-0055 addresses

> **Number provisional.** Per this repo's convention the ADR number is assigned at merge; renumber if `0060` is taken by a parallel branch.

> **Supersedes part of ADR-0055.** ADR-0055 made the reconnect *cadence* an injectable seam whose decision was `delay | give-up`. This ADR widens that decision to `retry | reset | give-up`, renames the seam to reflect that it now governs *recovery* (not just reconnect timing), and adds the device-reset mechanism the `reset` rung needs. The `GaveUp` terminal state, the observable `ConnectionState`, and the BCL-only / Polly-free stance from 0055 all stand.

> **Revision (2026-06-13, same day).** Decisions 6-7 and the incident analysis first diagnosed the missed self-recovery as watcher *event coalescing*. Reading the watcher source (`WindowsDeviceMonitorProvider`, `DeviceWatcher`) corrected that: the watcher is **edge-driven** — no debounce, no re-enumerate-and-diff — so coalescing was never the cause. The real mechanism is **no soft-deactivate on Windows + a `_knownConnectedIds` dedup that drops the re-activation**. **Decision 9** adds the presence-vs-health axis that follows from the correction; Decisions 6-7 are refined to match.

---

## Context

ADR-0055 gave `DeviceProxyBase` an injectable reconnect policy: after a session fails to open or drops, the policy returns the backoff delay, or `null` to **give up** → `ConnectionState.GaveUp`, where the proxy parks until the device **re-enumerates** (the `OnTrackerStateChanged(IsActive=true)` reset point clears `GaveUp` and re-opens). The kiosk injects `KioskReconnectPolicy`: 5 attempts, `min(15s, 2^(n-1))` backoff, then `GaveUp`.

That closed the infinite-retry and false-healthy gaps. But it left the recovery vocabulary with **one verb: re-open** — and re-opening cannot fix the failure mode we hit in the field.

**The incident (field, 2026-06-13).** A Treehopper LED controller's USB endpoint wedged — the device stayed *enumerated and healthy at the OS/PnP level* (`CM_PROB_NONE`, `Present=True`), but every session write timed out. The proxy retried 5 opens (re-opening a wedged endpoint is a no-op), hit `GaveUp`, and parked. A manual `Disable-PnpDevice`/`Enable-PnpDevice` **USB-stack reset healed the endpoint** — yet the proxy did **not** self-recover, because (a) `GaveUp` only wakes on a watcher-visible re-enumeration, and (b) a soft `Disable`/`Enable` produces **no** watcher-visible transition at all: Windows raises no soft-deactivate, a disable leaves the instance *enumerated* so no removal edge fires, and the re-enable's activation is then swallowed by the watcher's `_knownConnectedIds` dedup (the id was never cleared). The operator finally recovered it by toggling the lighting service Mock→Real, which **rebuilt the proxy and watcher from scratch** (fresh attempt budget, empty `_knownConnectedIds`) onto the now-healthy device — which is exactly why a full rebuild worked where a 3 s cycle did not. (The earlier reading of this as event *coalescing* was wrong; see the revision note above and **Decision 9**.)

Three lessons fall out:

1. **Re-open ≠ reset.** A wedged-but-enumerated endpoint needs its USB state *cycled*, which `DeviceProxyBase` cannot do. Re-opening the same handle forever is futile. This is the wedged-endpoint class — "present, but recoverable only by a power-cycle."
2. **Waiting for `GaveUp` to escalate is too late.** For a failure mode we *recognize*, grinding the full 5-attempt / ~31 s retry ladder before doing the thing that works is backwards. Recovery should be able to reset **early**, keyed on the fault.
3. **The fault is first seen at an IO call.** The wedge surfaces as a write/read timeout inside `WhileOpenAsync` (the supervised worker), which `RunWorkerAsync` already catches → records `_lastFault` → closes → funnels into the reconnect loop. The recovery decision already *has* the fault; it just can't act on it beyond timing.

So: add a device **reset** capability, and fold it into the recovery seam so the policy — which already sees the fault — can choose to reset, early, instead of only retrying or giving up.

## Decision

### 1. The reset mechanism lives in **core `Periphery`**, not a `Periphery.Management` extension

Core already owns the platform device layer: `DevNodeHelper` P/Invokes CfgMgr32 / SetupAPI (`CM_Register_Notification`, `SetupDiGetClassDevs`, the devnode-tree walk), and the `Windows/` and `MacOS/` providers live there too. The reset verbs (`CM_Disable_DevNode` + `CM_Enable_DevNode`, `IOCTL_USB_HUB_CYCLE_PORT`) are **siblings** of the enumeration and notification calls already in that file — same surface, no new dependency. Spinning them into a separate assembly would fracture one cohesive concern (the CfgMgr32 device layer) across two libraries for zero benefit. A `Periphery.Management` library is justified only if "management" grows into a real cluster — firmware update, power management, a device-admin surface — which is YAGNI for one primitive; Periphery's no-consumers stance makes a later extraction free.

### 2. Reset is a per-device **capability**, derived from transport — not a universal method

Not every enumerated device is USB-resettable: the same box carries PS/2 keyboard/mouse, a virtual AMT SOL COM port, network adapters, and MF cameras alongside the USB devices. So a device **advertises the reset strategies it can attempt**, and an **empty set is a first-class "not resettable" answer**:

```csharp
namespace Periphery;

public interface IDeviceReset
{
    /// Strategies available for this device, gentlest first. Empty ⇒ not resettable.
    /// Derived from the device's enumerator/bus-type — no open required, which matters
    /// because a GaveUp device has no live handle — plus any device-specific soft reset.
    IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device);

    /// Execute one strategy. Advertisement means "can attempt"; this may still degrade
    /// or fail at runtime (e.g. a hub with no per-port power switching).
    ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct);
}

public readonly record struct ResetStrategy(
    ResetKind Kind,           // SoftProtocol → UsbPortCycle → PnpDisableEnable (ascending force)
    ResetBlastRadius Radius,  // Self | SharedHub — does cycling this disturb siblings?
    bool ReEnumerates);       // does it produce the absent→present transition the wake path needs?

public enum ResetKind { SoftProtocol, UsbPortCycle, PnpDisableEnable }
public enum ResetBlastRadius { Self, SharedHub }
public enum ResetOutcome { Issued, Degraded, Failed, NotSupported }
```

**Where strategies come from — composed across the layers Periphery already has:**

- **Transport / platform (core).** Maps the devnode's `DEVPKEY_Device_EnumeratorName` (`USB` / `HID` / `BTHENUM` / `ACPI` / `ROOT` / `SWD` …) to the hard strategies. `USB` — or a node with a USB ancestor; the platform layer **walks to the resettable ancestor** for bridged devices like the USB-serial payment terminal or the HID-as-COM scanner — yields `{UsbPortCycle, PnpDisableEnable}`. Non-USB transports yield `{}` or an adapter-level reset.
- **Device extensions** (`Periphery.Treehopper`, `.Camera`, `.Hid`). May *add* a `SoftProtocol` strategy their firmware/driver supports — a board reset command, an MF source reinit — gentler than cycling the bus.

Availability is part **static** (transport → conceivable) and part **runtime** (is the parent hub/port resolvable? does the hub support power switching?), which is why advertisement means "can attempt" and `ResetAsync` returns a `ResetOutcome` rather than asserting success.

### 3. Fold reset into the **single recovery seam** — one fault-aware policy, not a separate coordinator

Widen ADR-0055's decision from `delay | give-up` to `retry | reset | give-up`, and rename the seam to reflect that it now governs recovery rather than reconnect timing:

```csharp
public interface IRecoveryPolicy   // supersedes IReconnectPolicy (ADR-0055)
{
    ValueTask<RecoveryDirective> DecideAsync(RecoveryContext context, CancellationToken ct);
}

public readonly record struct RecoveryContext(
    int Attempt,                                   // consecutive open-failures this cycle; resets on re-enumeration
    int ResetCount,                                // resets since the last stable open — the reset budget
    Exception? LastFault,                          // the IO/open fault driving the decision — the reset-early signal
    DeviceInfo Device,
    IReadOnlyList<ResetStrategy> AvailableResets); // empty ⇒ Reset is not an option for this device

public abstract record RecoveryDirective
{
    public sealed record Retry(TimeSpan Delay)       : RecoveryDirective;
    public sealed record Reset(ResetStrategy Strategy): RecoveryDirective;  // policy picks from AvailableResets
    public sealed record GiveUp()                    : RecoveryDirective;
}
```

The policy **sees the fault** and can **reset early**: for a recognized wedge signature it returns `Reset` on attempt 1 (after at most one sanity retry to rule out a blip); for an unknown fault it walks the retry ladder as today; when the reset budget is spent it returns `GiveUp`. The whole curve lives in one injected place:

```csharp
sealed class KioskRecoveryPolicy : IRecoveryPolicy   // consumer side; supersedes KioskReconnectPolicy
{
    public ValueTask<RecoveryDirective> DecideAsync(RecoveryContext ctx, CancellationToken ct)
    {
        // Known wedge: the Treehopper write-timeout signature. Don't grind the ladder.
        if (LooksLikeEndpointWedge(ctx.LastFault) && ctx.AvailableResets.Count > 0 && ctx.ResetCount < 2)
            return new(new RecoveryDirective.Reset(GentlestSelfScoped(ctx.AvailableResets)));

        if (ctx.Attempt <= 5)                                       // unknown fault: retry ladder
            return new(new RecoveryDirective.Retry(Backoff(ctx.Attempt)));

        if (ctx.AvailableResets.Count > 0 && ctx.ResetCount < 2)    // ladder spent: reset before conceding
            return new(new RecoveryDirective.Reset(GentlestSelfScoped(ctx.AvailableResets)));

        return new(new RecoveryDirective.GiveUp());                 // → GaveUp → #160 Unhealthy → human
    }
}
```

This is deliberately **not** a coordinator reacting to `GaveUp` (Alternative A). A GaveUp-reactor can only fire *after* the retry budget is burned, so it structurally cannot reset early — the property the known failure mode needs most.

### 4. Cross-device concerns are met by **injection**, not a coordinator layer

Reset has properties retry doesn't — blast radius, system-state safety, a budget. Each is handled without a new layer:

- **Blast radius / shared hub** → the **mechanism** coalesces. `IDeviceReset` owns the devnode tree, so a `UsbPortCycle` (which can disturb siblings on the same hub) dedups concurrent cycles: "this hub was just cycled for a sibling, fold in." Physical-coordination state lives with the thing performing the physical act.
- **Safety (don't reset mid-transaction)** → a narrow injected predicate the proxy consults before executing a `Reset`:
  ```csharp
  public interface IResetSafetyGate { ValueTask<bool> CanResetAsync(DeviceInfo device, CancellationToken ct); }
  ```
  One boolean dependency (default: always-safe), closing over whatever system state the consumer cares about (the kiosk's "is a sale in progress") — DI of a decision, not a live feed of the whole system.
- **Budget + escalation** → `ResetCount` in the context; when it's spent the policy returns `GiveUp` → the existing health / OutOfService path. No new escalation machinery.

### 5. `Recover(Exception fault)` on the proxy — exposed now

The wedge first appears at an IO call. Worker IO already funnels in; for consumers doing **direct** IO on the open device, give them the same entry point rather than a parallel reset path:

```csharp
// DeviceProxyBase
/// Funnel a consumer-observed fault into the recovery lifecycle: record it as the
/// LastFault, close the current session, and run the recovery seam (which may retry,
/// reset, or give up per the injected policy). The same path worker faults take.
public void Recover(Exception fault);
```

It is exposed **now** (not deferred) to learn the ergonomics in practice. It must funnel into the **same** close→recover lifecycle — never a side path that resets behind the proxy's back.

### 6. The reset feeds back through the **existing re-enumeration wake path**

> **Refined by Decision 9.** Self-driven re-open is the *primary* path — the proxy that issued the reset re-opens on its own authority. The wake path below is a fast-path **accelerator** that only `ReEnumerates: true` strategies trigger; a `ReEnumerates: false` strategy fires no watcher transition, so it relies entirely on self-driven re-open.
>
> **Corrected (2026-08-07, `#232` measurement).** This paragraph used to name `SoftProtocol` alongside `PnpDisableEnable` as a `ReEnumerates: false` kind. That is **measured to be false** for the soft reset this repo actually ships. `treehopper-flash reboot` now watches OS device notifications instead of polling, and a Treehopper board reset (wire opcode `0x0C`) leaves the USB bus ~15 ms after the write and returns at ~245 ms — an absence of ~230 ms, with real remove/arrive edges, reproduced on two boards and corroborated by `DEVPKEY_Device_LastArrivalDate`. The old claim was never load-bearing in code: `TreehopperDeviceReset` has always declared `ReEnumerates: true`, so the ADR and the implementation disagreed and the implementation was right.
>
> The general correction is the one that matters: **`ReEnumerates` is a property of the individual strategy, not of its `ResetKind`.** It is a constructor argument on `ResetStrategy` for exactly that reason. `PnpDisableEnable` genuinely never re-enumerates (the instance stays in the tree) and a real `UsbPortCycle` genuinely does, but `SoftProtocol` depends entirely on what the firmware does with the command — a board reset re-enumerates, an MF source reinit does not. Read the flag; do not infer it from the kind.
>
> **Amended (2026-08-11, `#251` measurement): `ReEnumerates: false` is confirmed for `PnpDisableEnable`, and a reset must not report done while the device is still coming back.** Measured across disable/enable cycles on a Treehopper: a `DeviceWatcher` filtered to the device's own LocationPath + serial fired **no edge of any kind** (0/5 trials), and the device **never left enumeration** — it only flipped `Disabled`/`CM_PROB_DISABLED` → `OK`/`CM_PROB_NONE`. So the flag is honest, and the tempting "fix" of flipping it to `true` to buy the event-driven post-reset wait is a **trap**: with the device continuously enumerated, an identity-filtered wait matches the *still-disabled* node from its own startup snapshot and returns immediately. It would add latency and guarantee nothing. Polling that snapshot is no better — measured at ~940 ms per tick with ~1 s of lag, reporting a match while the device was unopenable, and `.Active(true)` did not discriminate.
>
> What follows is a duty on the **mechanism**, not the caller. Because this rung produces no edge, a caller has nothing to wait on and degrades to a blind delay — which is exactly how a healthy fleet board got declared beyond recovery: `BootloaderEntryOrchestrator` waited a flat 750 ms after the reset, the driver stack on loaded kiosk hardware had not finished reloading, the retry's open threw `UsbDeviceNotFoundException`, and the wasted attempt eventually exhausted the recovery budget. **`IDeviceReset.ResetAsync` therefore carries a best-effort obligation to return only once the device is back**, wherever the platform can cheaply observe it. On Windows that is `CM_Get_DevNode_Status`: `DN_STARTED` with `CM_PROB_NONE` costs **~0.07 ms** to probe and precedes actual WinUSB openability by a tight **14.9–16.2 ms**, so `WindowsDeviceReset.DisableEnableAsync` now polls it (25 ms interval, **2 s** bound) instead of returning the instant `CM_Enable_DevNode` does. The residual ~16 ms is interface-arrival lag that callers' existing settle margins already dwarf.
>
> **The bound is two-sided, and the ceiling is the fleet constraint.** It must clear the 750 ms that failed (2 s is ~2.7×, and ~20,000× the measured healthy path), but it is not free to overshoot: Treehopper is an EFM8 **no-serial family** — every unit's bootloader enumerates as the shared `0x10C4:0xEAC9` — so `FlashAnythingService` gates the entire reboot → correlate → flash window **one board at a time**, and a stall here blocks the box's *other* boards. The kiosk watcher is simultaneously racing the kiosk's own claim on a margin measured in seconds (won at boot+40.5 s, lost at boot+41 s). A per-board stall therefore multiplies directly into lost boot races: at a 10 s bound, ~30 s on a 3-board box. Raise it only against a measured restart distribution, never on intuition.
>
> This does **not** promote `ResetOutcome.Issued` into a health verdict — ADR-0073 still stands. The dividing line is **what the rung can observe**: a rung that *cannot* confirm anything (the ADR-0075 EP0 rescue, where a resetting device and one that ignored the request fault identically) still reports `Issued` without waiting, because that is *absence of confirmation*. A rung that *watched and saw the device fail to come back* reports `Failed` — that is *evidence of non-recovery*, and reporting success on it would be the same over-claiming this amendment exists to remove. `DisableEnableAsync` therefore returns `Failed` when its readiness poll times out, not `Issued`.

A reset is a "self-replug." After issuing a `Reset`, the reconnect loop **returns** and lets the device's drop-and-return drive the re-open through the path that already handles a human replug — `OnTrackerStateChanged(IsActive=true)` clears the transient state and re-opens. The proxy needs no new re-open logic; the reset only has to *cause* the transition. Two guards:

- **A `Resetting` connection state.** A real cycle makes the watcher fire absent-then-present, which would itself try to close/open. `Resetting` + the existing open-lock / `IsOpen` checks keep `OnTrackerStateChanged` the single owner of the re-open.
- **A timeout backstop.** If the device isn't `Open` within ~10 s of a reset, the proxy re-runs the recovery seam (→ another reset or give-up), so a missed re-enumeration strands nothing.

### 7. Dependency: the watcher must not drop the post-reset re-activation (`#259`) — mechanism corrected

The accelerator path (Decision 6) relies on the watcher delivering the post-reset activation. **`#259` is not event coalescing.** The `CM_Register_Notification` watcher is edge-driven — no debounce, no re-enumerate-and-diff against `KnownDevices`. The real gap is twofold: Windows raises no soft-deactivate edge, and the watcher's `_knownConnectedIds` set **drops a `DeviceActivated` whose id is still present** (it assumes the matching down-edge always fired first). So a re-activation after a missed or absent down-edge is silently swallowed. The fixes: (a) prefer `ReEnumerates: true` strategies, which actually fire `DEVICEINSTANCEREMOVED → …STARTED`; (b) make the watcher **missed-edge-tolerant** so a `DeviceActivated` for an already-known id *re-asserts* (re-resolves the tracker) instead of being dropped; (c) per Decision 9 recovery is self-driven, so the wake path is no longer load-bearing for *correctness* — only for *latency*. The dormant interface filter (Decision 9, secondary) is related. Tracked as `#259`.

### 8. Forward the mechanism + policy through the session-host layer

As ADR-0055 amendment #2 forwarded `IReconnectPolicy` through `DeviceSessionHost` / `MultiDeviceSessionHost`, the `IRecoveryPolicy`, `IDeviceReset`, and `IResetSafetyGate` flow down the same factories (per-device, fanned out in the multi-host), with default-null preserving prior behavior.

### 9. Presence ≠ health: health is an IO-derived, proxy-owned axis; OS notifications are accelerators

The incident exposed a deeper conflation. Periphery exposes **one** axis — `DeviceActivityStatus` (`Absent` / `Present` / `Active`) — and all three are *device-tree* facts sourced from OS notifications: "is it enumerated and started?" There is no notion of **functional health**: "does IO actually work?" The field wedge was `Active` (enumerated, `CM_PROB_NONE`) the whole time it was functionally dead. Treating tree-presence as a stand-in for health is the root cause under `#156` / `#160` / `#259` — and registering *more* OS notifications cannot fix it (Alternative E): no device-tree notification can observe a wedged-but-enumerated endpoint, because the OS itself does not know.

Presence and health are two orthogonal axes with two different sources, and Periphery must keep them separate:

- **Presence / liveness** — owned by the OS, delivered by **push** (the `CM_Register_Notification` watcher). Cheap, but it can only ever report tree membership. It cannot see a wedged endpoint, a hung driver, or silent corruption.
- **Functional health** — knowable **only** by issuing IO and observing the outcome. A **pull/probe** signal, and the only component positioned to produce it is the one doing the IO: the proxy/session.

This ADR therefore adds:

1. **The IO fault is the authoritative health signal.** Worker IO already funnels into `RunWorkerAsync` → `_lastFault` → close → recover; `Recover(fault)` (Decision 5) gives direct-IO consumers the same door. That fault — **not** the tracker's `IsActive` — is ground truth that the device is unusable, and it is what `IRecoveryPolicy.DecideAsync` keys on. It is the only signal that ever detects a wedge.
2. **Health is read from the proxy, presence from the tracker.** A consumer asks the **proxy** (`ConnectionState`: `Open` / `Reconnecting` / `Resetting` / `GaveUp`) *"is it working?"* and the **tracker** (`DeviceActivityStatus`) only *"is the cable even in?"* The kiosk false-healthy bug (`#160`) is exactly an overload of `IsActive` for both questions; this ADR forbids that overload.
3. **Notifications are accelerators, never the health source — and silence is never evidence of health.** A real removal edge is a cheap *"stop trying now"*; a real arrival is a cheap *"try reconnecting now."* Watcher edges *accelerate* the proxy's state machine. The inversion that wedged us — *no disconnect event ⇒ still healthy* — is banned: recovery never waits on, nor infers anything from, watcher silence.
4. **Recovery is self-driven from the fault** (the primary statement Decision 6 refines). The proxy that issued a reset *knows* it just reset; it re-opens on its own authority and treats a watcher wake as a fast-path, not a precondition. This keeps recovery correct for `ReEnumerates: false` strategies and when the watcher misses an edge.
5. **Optional active liveness probe (opt-in, off by default).** A device that wedges while *idle* (a kiosk writing LEDs every few seconds) is not detected until its next command. A consumer may inject a cheap periodic probe (no-op read / HID feature-get / vendor ping) so the wedge surfaces proactively — the device analogue of a liveness probe. Per the functional-core / imperative-shell split, the probe's **cadence and verdict are pure core**; the **clock and IO are the shell**.

**Secondary correctness fix (an accelerator, not the cure).** The Windows interface-notification registration is currently **dormant** — `ClassGuid = Guid.Empty` with no `CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES` (the flag is not even defined in-tree) — so the interface filter's arrivals/removals never fire and the instance filter carries everything. Making it live lets a soft disable/enable emit interface edges, closing the disable-visibility gap and improving accelerator latency. It does **not** help the wedged-endpoint case — nothing in the notification model can — which is why it sits *under* the IO-derived health axis, not in place of it. Verify and fix under `#259`.

### 10. Clear the reset budget on a **stable-open dwell**, not the instant open returns

`RecoveryContext.ResetCount` (Decision 3) is the reset budget the policy escalates over and ultimately concedes at (`GiveUp` → `GaveUp`, Decision 9). The budget is only meaningful if it is cleared at the *right* moment. The first implementation cleared it (and `_lastFault`) the instant a successful open returned — the moment `OpenDeviceAsync` + `OnActivatedAsync` completed. That is **too early** for the exact failure mode this ADR exists to fix.

**The flaw.** Opening a wedged device often succeeds: the open path only exercises a *healthy* endpoint (for the motivating Treehopper LED board, `ConfigureDevice` touches the CONFIG endpoint while the wedge is on the DATA endpoint), so the proxy reaches `Open`, zeroes the budget, and starts the supervised worker. The first real IO in `WhileOpenAsync` (the first SPI write) then faults ~2s later on the wedged endpoint, funneling through `RunWorkerAsync` → close → reconnect. Because the budget was already cleared to 0, the recovery ladder **restarts at strategy [0] every cycle**. The consequences:

- It **never escalates** past the gentlest reset strategy.
- It **never reaches `GaveUp`**, so the "enumerated but unopenable; needs a human / power-cycle" signal (the whole point of the `GaveUp` → Unhealthy mapping in consumers) never fires.
- For a **re-enumerating** first strategy (e.g. the Treehopper `SoftProtocol` board reboot, added as the gentlest rung) it becomes a **self-made infinite reset loop**: reset → re-enumerate → reopen succeeds → budget cleared → worker re-faults ~2s later → reset again, forever. Escalation only ever worked by the *accident* that some strategies' self-driven reopen (Decision 6) times out, preserving the budget; a strategy whose reopen succeeds-then-refaults defeated escalation entirely.

**The fix.** Keep `SetState(Open)` immediate — health / openability reporting (Decision 9: `Open` → Healthy) must stay correct the instant the device opens — but **defer** clearing `ResetCount` / `_lastFault` until the session has actually *survived* a configurable **stable-open dwell** (`StableOpenDwell`, default 5s — comfortably outlasting the ~2s post-open refault, well under the 10s `ResetReopenTimeout`). A session that faults, closes, is reset, or is disposed before the dwell elapses **preserves its budget**, so the ladder keeps escalating and ultimately concedes. Only a session that proves it can hold open clears the budget and starts the next, unrelated fault from strategy [0]. This is consistent with the functional-core / imperative-shell split (Decision 9.4, ADR-0052): the dwell is pure timing owned by the shell (`Task.Delay` on the connection token + `Environment.TickCount64`), advancing budget state the policy reads as a value — no clock leaks into the policy.

**Lifecycle / race guard.** The dwell is keyed to the **live connection generation** (a monotonic id bumped under the open lock per session) and waits on the **per-connection token** (`_connectionCts`, already cancelled on every close / fault / reset / dispose). A close cancels the delay → the dwell returns without clearing. If the delay does elapse, the dwell re-acquires the open lock (cancellable on the same token) and clears **only if** the proxy is not disposed, the generation still matches, and the session is still `Open` — so a stale timer can never zero a newer connection's budget, and nothing clears after disposal. The dwell task is launched *inside* the open lock so its `Task.Delay` registration runs while the CTS is guaranteed alive (closing the launch-vs-dispose window); it yields immediately at the delay and does not hold the lock.

**Interaction with the re-enumeration clear (Decision 6 / the `OnTrackerStateChanged` GaveUp exit).** That clear — a genuine external replug while parked in `GaveUp` — is **kept unchanged**. It is orthogonal: it can only run *from* `GaveUp`, a state with no open session and therefore no pending dwell, so the two can neither double-clear nor cancel one another. A replug is a real fresh budget; the dwell is a proven-stable budget; both are correct, and they never overlap in time.

### 11. Recover an enumerated-but-faulted node that never reaches Active

Decisions 1-10 all hang off **one** entry point: a session fails to **open** (or drops) on a device the tracker reports as `Active`. `DeviceProxyBase.OnTrackerStateChanged` only attempts an open on `state.IsActive`. But the field hit a failure mode that never reaches that entry point at all.

**The incident (field, second occurrence).** A Treehopper LED board enumerated `DeviceInfo.Status == Error`, cfgmgr32 problem code **21** (pending-removal / failed-post-start) **from boot** — it never became `Active`. The tracker maps a matched-but-not-active device to `ActivityStatus = Present` — the **same bucket** as a healthy paired-but-out-of-range Bluetooth device — so the proxy never attempted an open, the recovery ladder (Decisions 3-10) never ran, and the board sat dead for 45+ minutes until a manual elevated `Disable-PnpDevice` + `Enable-PnpDevice` cleared the devnode to `Status=OK`, after which the kiosk opened the session normally. The reset that fixes this — `IDeviceReset` / `WindowsDeviceReset` `PnpDisableEnable`, which needs **no open handle** (Decision 2) — already existed in Periphery; nothing was wired to *fire* it for a node that never opened.

So this decision adds a **second, symmetric trigger** into the same recovery seam: "enumerated-but-faulted and never-ready," alongside the existing "open-failed on an Active device." It deliberately reuses every piece of the Decision 3-10 machinery rather than building a parallel path.

**The trigger.** When the tracker holds a matched device that is `Present` (not `Active`), the proxy gives it a short **settle window** (default **3s**, overridable) — a freshly-enumerated node can report a transient problem code for a moment while its driver finishes starting, and must be allowed to reach `Active` on its own first — then, if it is still a genuine fault and has never reached a stable open this cycle, it drives the same reset ladder. The settle window is shell-owned timing (`Task.Delay` on the dispose token); 3s is comfortably past a normal driver bring-up's transient-problem window yet short relative to the 45-minute stranding it replaces.

**The classification (the load-bearing safety rule).** `Present` is a **legitimate steady state**; blanket-resetting every non-Active device would be far worse than the bug. The trigger is gated by a **pure, total classification function** (`DeviceFaultClassifier.IsResettableFault`, per the ADR-0052 functional-core split — exhaustively unit-testable with hand-built `DeviceInfo` values, no hardware):

- Cross-platform signal: **`DeviceStatus.Error`** (which Windows / Linux / macOS all set) is the trigger.
- The Windows `RawStatus` (`CM_PROB_*`) problem code is used **only to refine / exclude**, never to broaden:
  - **`Status == Disabled`** → never auto-enable. An intentional user/policy state; resetting it fights the operator.
  - **problem code `22` (`CM_PROB_DISABLED`)** → never, the Windows-granular form of the same hands-off rule, even if a provider mapped the coarse status differently.
  - **problem code `0` (`CM_PROB_NONE`)** → not a fault. The OS says there is no problem; that is authoritative over a stale coarse status (a healthy `Present` Bluetooth device — paired, out of range — sits here and is left strictly alone).
  - otherwise **`Status == Error`** (any non-zero, non-disabled code — 10 failed-start, 21 pending-removal, 31 driver-failed, 43 reported-problem, ...) → a resettable fault candidate.
- Non-Windows targets that carry no problem code fall back to `Error` as the signal and `Disabled` as hands-off, so the trigger behaves sanely everywhere.

**Reuse of the ladder + bounds (no parallel reset path).** The decision still routes through `IRecoveryPolicy.DecideAsync`. `RecoveryContext` gains a `Trigger` discriminator (`OpenFailure` | `EnumeratedFault`, defaulting to `OpenFailure`) so a policy can tell the two causes apart, but it is the **same** policy, the **same** reset mechanism, the **same** reset budget (`ResetCount`), the **same** escalation-to-`GiveUp`, and the **same** stable-open dwell (Decision 10). A faulted-node "retry" directive means "wait, then re-check whether the node cleared to `Active` on its own" — there is no healthy handle to re-open — while "reset" runs the existing `TryResetAndReopenAsync` (which self-drives the re-open once the node comes up Active, Decision 6/9). A node that keeps re-faulting climbs the budget and converges to `GiveUp` (→ the Unhealthy / human-dispatch signal) instead of reset-looping forever. Once the node reaches `Active`, the loop hands off to the normal open path.

**Opt-in (mechanism/policy split, Decision 3).** Some consumers must not have Periphery cycling devnodes uninvited, so the *trigger* is gated by an explicit **`faultedNodeRecovery` flag (default `false`)** on `DeviceProxyBase` (forwarded through `DeviceProxy` / `DeviceProxy<T>` / `DeviceSessionHost` / `MultiDeviceSessionHost`, per Decision 8). With the flag off — the default for every existing consumer — `OnTrackerStateChanged` is byte-for-byte unchanged: the proxy only ever acts on an `Active` device. The flag enables the trigger; the injected policy still owns the decision; the classifier still gates on a genuine fault. Three independent gates, none of which the default consumer crosses.

This is the third piece of the same incident's fix: a stable-open dwell (Decision 10) stopped escalation being defeated by a fast refault, a kiosk-side health-grace change made a boot-dead device *surface* as Unhealthy, and this makes Periphery actually **self-heal** the faulted devnode instead of needing a human disable/enable.

## Alternatives considered

- **(A) A coordinator one layer up, reacting to `GaveUp`.** Observe devices hitting `GaveUp` and drive resets from a cohort/orchestration layer that holds the topology + system view. *Rejected as the primary shape:* it can only act after the retry budget is exhausted, so it cannot reset early for a known failure mode — the property that matters most here. Its one genuine edge — a **whole-hub glitch** that downs N devices, where one deliberate hub-cycle beats N independent decisions — is deferred; it composes *on top* later, observing the same `GaveUp`/`ConnectionState` signals, non-breaking. So starting with the single seam costs nothing.
- **(B) A separate `Periphery.Management` library.** Rejected: fractures the CfgMgr32 device layer that already lives in core; YAGNI for one primitive (see Decision 1).
- **(C) Reset at the literal IO call site.** Rejected: bypasses the proxy lifecycle and forces IO code to cope with the device vanishing mid-call — the exact problem the proxy exists to own. `Recover(fault)` funnels the IO-site signal into the lifecycle instead (Decision 5).
- **(D) A universal `ResetAsync()` on every device.** Rejected: not all devices are resettable (PS/2, virtual, network); the `StrategiesFor` capability makes "not resettable" the empty set rather than a method that lies (Decision 2).
- **(E) Detect the failure by subscribing to more OS notifications** (a live interface filter, a PnP problem-state / `Disabled` action). *Rejected as the primary fix:* no device-tree notification can observe a wedged-but-enumerated endpoint — the OS does not know it is wedged — so this cannot detect the failure that motivates the ADR. `CM_Register_Notification` has no "disabled/stopped while present" action at all; PnP problem-state (`CM_Get_DevNode_Status`) is a *pull*, read on fault for diagnosis, not a push. **Adopted only as a secondary accelerator** — fixing the dormant interface filter (Decision 9) — under the IO-derived health axis, never as a substitute for it.

## Consequences

- **+ Hard wedges self-heal.** The field Treehopper wedge recovers unattended: fault → reset → re-enumerate → reopen, with no operator Mock→Real toggle and no manual `Disable-PnpDevice`.
- **+ Faulted-from-boot nodes self-heal too (Decision 11).** A device that enumerates faulted and *never reaches Active* — the second field incident, dead from boot for 45+ minutes — now drives the same reset ladder after a short settle window, opt-in and gated by a pure fault classifier so a healthy or user-disabled `Present` device is never touched. The recovery seam is now symmetric: it fires on "open-failed on an Active device" **and** on "enumerated-but-faulted and never-ready."
- **+ Reset-early on known faults.** No ~31 s of futile re-opens standing in front of the cure.
- **+ `GaveUp` finally means "genuinely needs a human"** — retries *and* resets exhausted — so OutOfService + the fleet alert become a true signal, not a false alarm for something the box could fix itself.
- **+ One seam.** The recovery vocabulary (retry / reset / give-up) lives in one injected policy; the reset mechanism is one core capability; no new coordination layer to reason about.
- **+ A real health axis.** Functional health (IO-derived, proxy-owned) is split from tree-presence (watcher-owned), closing the false-healthy class (`#160`) at its source: a wedged-but-enumerated device reads as `Reconnecting` / open-failed on the proxy, not `Active`-and-trusted on the tracker.
- **+ Recovery no longer hinges on the watcher.** Self-driven re-open (Decision 9) makes reset feedback correct even when a strategy doesn't re-enumerate or the watcher misses the edge; the wake path is latency, not correctness.
- **− Breaking API surface:** `IReconnectPolicy` → `IRecoveryPolicy` (return widens to a directive), `ReconnectContext` → `RecoveryContext` (+ `ResetCount`, `AvailableResets`), new `IDeviceReset` / `ResetStrategy` / `IResetSafetyGate`, a `Resetting` state, and `Recover(fault)`. Fine under Periphery's no-consumers / breaking-changes-fine stance; update `DeviceProxyBase`, the session-host forwarding, and the consumer policy (the kiosk's `KioskReconnectPolicy` → `KioskRecoveryPolicy`).
- **− The mechanism carries cross-device state** (recent hub cycles, for coalescing). A mild smell, but it is the right home for physical-coordination memory; revisit if the whole-hub coordinator (Alt A) ever lands.
- **− `#259` is reframed, not a hard dependency.** With self-driven recovery (Decision 9) the watcher is no longer load-bearing for reset *correctness*; `#259`'s real fix is making the watcher missed-edge-tolerant (Decision 7), which now affects only recovery *latency*.
- **− A dormant interface-notification registration to fix** (Decision 9, secondary). Cheap correctness debt the watcher already carried; closing it improves accelerator latency but does not affect wedge detection.
- **Out of scope — a true power-cycle guarantee.** `UsbPortCycle` only cuts power if the hub supports per-port switching; otherwise it degrades to a soft reset (reported via `ResetOutcome.Degraded`). The deepest MCU locks may still need a physical replug. This shrinks the human-dispatch set; it does not eliminate it.
- **Out of scope — whole-hub coordination** (Alt A) and the **per-attempt open timeout** (still out, per ADR-0055).
