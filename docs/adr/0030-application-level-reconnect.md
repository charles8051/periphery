---
title: "ADR-0030: Application-Level Reconnect — Silent Device Failure Without OS State Transition"
status: "Accepted"
status_note: "Shipped - `DeviceProxyBase` / `DeviceProxy`, with injectable recovery policies ([ADR-0055](0055-injectable-reconnect-policy.md))."
date: "2026-07-16"
amended: "2026-07-16"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "lifecycle", "reconnect", "device-handle", "serial", "i/o", "resilience"]
supersedes: ""
superseded_by: ""
---

# ADR-0030: Application-Level Reconnect — Silent Device Failure Without OS State Transition

## Status

> **Amendment (2026-07-16).** Post-implementation lifecycle audit identified
> additional defects beyond the original reconnect gap: (1) normal loop return
> does not currently close or reconnect, despite ADR-0027 documenting that it
> should; (2) initial open / init-gate failures while the tracker remains active
> do not currently retry; (3) per-connection `CancellationTokenSource` instances
> leak on failed activation paths; (4) consumer event handlers and teardown hooks
> can currently abort cleanup and leave the state machine half-torn-down; and
> (5) self-owned watcher startup can leak resources when `watcher.StartAsync()`
> throws. This amendment expands the implementation plan to close those gaps.

---

## Context

### 1. The reconnect contract as designed

ADR-0027 introduced `DeviceProxyBase<TDevice, TException>` and its delegate-configured
siblings (`DeviceProxy<TDevice>`, `DeviceProxy`). The handles promise
*reconnect-resilient lifecycle management*: when a device disconnects, the handle closes
the platform device; when it reconnects, the handle reopens it transparently.

This contract is delivered via the `DeviceTracker.StateChanged` event chain:

```
OS fires disconnect → DeviceWatcher delivers event → DeviceTracker.StateChanged fires
  → OnTrackerStateChanged: state.IsActive == false → CloseDeviceAsync()

OS fires reconnect  → DeviceWatcher delivers event → DeviceTracker.StateChanged fires
  → OnTrackerStateChanged: state.IsActive == true  → TryOpenDeviceAsync()
```

Both legs of the contract depend on the OS reporting a state transition.

### 2. The devnode filter and what it covers

`Activated` is gated on `DevNodeHelper.IsDeviceConnected`, which requires both
`DN_STARTED` (kernel-mode driver loaded and running) and the absence of
`DN_DEVICE_DISCONNECTED`. The problem code from `CM_Get_DevNode_Status` is also
checked — any code indicating a failed or resource-conflicted driver suppresses
Activation entirely. This means that the entire class of *driver-level* failures
(failed start, driver unload, device manager disable/enable cycle) is handled
correctly by the existing OS event path:

| Scenario | OS sees | Handle sees |
|---|---|---|
| USB serial adapter unplugged | `DN_DEVICE_DISCONNECTED` set → Deactivated fires | `CloseDeviceAsync` → `TryOpenDeviceAsync` on replug ✅ |
| Driver restart (devmgmt "Disable → Enable") | `DN_STARTED` clears → Deactivated; restores → Activated | `CloseDeviceAsync` → `TryOpenDeviceAsync` ✅ |
| Driver load failure / problem code set | `DN_STARTED` never set → Activated never fires | Handle never opens; recovers when driver starts ✅ |

### 3. The gap: application-protocol failure on a driver-healthy device

A narrower failure mode remains: the devnode passes all OS checks (`DN_STARTED`,
no problem code, not disconnected), but the connection fails at the
application-protocol or serial-link layer. These scenarios are invisible to the
OS and therefore produce no `StateChanged` event:

| Scenario | Why the OS stays silent |
|---|---|
| **Bluetooth SPP virtual COM port loses its radio link** | `bthmodem.sys` / the RFCOMM driver remains `DN_STARTED`; the virtual COM port is always "Active" from the devnode perspective regardless of radio connectivity. This is the most common real-world case. |
| **Embedded device crashes or power-cycles while USB adapter stays up** | The FTDI/CH340/CP210x USB adapter is `DN_STARTED` and physically connected. The device on the other end of the serial wire has gone away; the adapter has no way to report this to the OS. |
| **Serial-over-TCP virtual COM port driver loses its network endpoint** | The virtual port driver (com0com, HW VSP, etc.) is `DN_STARTED`; the remote TCP host dropping does not propagate to the devnode. |

In every silent-failure scenario:

1. The `onLoop` / `OnLoopAsync` delegate throws a non-`OperationCanceledException`.
2. `RunLoopAsync`'s catch block calls `CloseDeviceAsync` — the device is closed and
   `IsConnected` becomes `false`. This part works.
3. After `CloseDeviceAsync` returns, `RunLoopAsync` exits.
4. **The tracker state never changed.** `StateChanged` does not fire. `TryOpenDeviceAsync`
   is never re-invoked. The handle sits in `IsConnected = false` indefinitely.

### 4. The broken comment

`RunLoopAsync` in `DeviceProxyBase` (line 257) already has a comment that names the
intended behaviour:

```csharp
catch
{
    // Loop exited unexpectedly — trigger close + reconnect.
    _ = CloseDeviceAsync();
}
```

The comment says *"close + reconnect"*. The implementation only closes. This is the
defect this ADR resolves.

The non-generic `DeviceProxy.RunLoopAsync` contains the same catch block and the
same gap (no reconnect after `DeactivateAsync`).

### 5. Post-implementation audit findings

Review of `DeviceProxyBase` and the non-generic `DeviceProxy` after the first
ADR-0030 implementation surfaced several additional lifecycle defects that are
independent of the original silent-failure trigger.

| Finding | Current behaviour | Why it is a bug |
|---|---|---|
| **Normal loop return leaves the handle connected** | `RunLoopAsync()` only closes/reconnects on exception. If `OnLoopAsync()` / `onLoop` returns normally, the open device remains assigned and `IsConnected` remains `true`. | ADR-0027 explicitly states that *returning normally or throwing* from the loop should trigger close + reconnect. The current implementation violates that contract and can leave the handle in a zombie-connected state with no active loop. |
| **Initial open / init-gate failures do not retry** | If `OpenDeviceAsync()` or `onActivated`/`OnConnectedAsync()` fails during the first OS-driven activation, `OpenFailed` may fire, but no reconnect attempt occurs while the tracker remains active. | The handle becomes stuck until some external OS transition re-fires `Activated`, even though the device may still be active and the failure may have been transient. |
| **Per-connection CTS leaks on failed activation** | `_connectionCts` is created before open/init work, but failed pre-connected paths return without disposing it. | Repeated failures overwrite the field and leak `CancellationTokenSource` instances; more importantly, the handle's internal ownership model becomes inconsistent because `_connectionCts` survives even though no connection was established. |
| **Consumer event handlers can break lifecycle cleanup** | `DeviceOpened` / `DeviceClosed` are invoked inline with no protective boundary. | A throwing event handler can prevent subsequent lifecycle work from running, including loop startup, teardown, or disposal. Consumer notification must not be able to corrupt internal invariants. |
| **Teardown hooks can abort disposal** | `OnDisconnectingAsync()` / `onDeactivated` runs before disposing the device / clearing state, but exceptions are not isolated. | A throwing teardown hook can leave a device half-closed, CTS undisposed, and the handle internally inconsistent. Teardown hooks should be best-effort, not authoritative over cleanup. |
| **Owned watcher startup can leak** | `OpenAsync(...)` creates a watcher and handle, then awaits `watcher.StartAsync()`. If that throws, the handle never gets returned and the owned watcher is never disposed. | This violates the ownership contract for self-contained handles and leaks registrations / subscriptions on startup failure. |
| **Typed open-failure handling in the base class is brittle** | `DeviceProxyBase<TDevice, TException>` catches only `TException` from `OpenDeviceAsync()`. Other exception types fault the fire-and-forget task. | This is acceptable for `DeviceProxy<TDevice>` where `TException == Exception`, but extension-package derived handles can accidentally leak unexpected exceptions out of the lifecycle task. |

### 6. Why application-level reconnect is the library's responsibility

The handle's stated purpose is *reconnect-resilient lifecycle management*. Reconnect
resilience has always meant: *"the handle recovers from transient failures without
consumer intervention."* Requiring consumers to:

- Subscribe to `DeviceClosed`,
- Inspect the tracker to determine why it closed,
- Call some `ReconnectAsync()` method at the right moment, and
- Reason about whether the handle is in a reconnectable state

…defeats the purpose of the abstraction. A serial port that drops its connection for
two seconds and comes back is a routine embedded-device behaviour, not an exceptional
condition that should require application-level code.

### 7. Reconnect conditions and constraints

A retry must only proceed when all of the following hold:

| Condition | Rationale |
|---|---|
| The handle is **not disposed** | Disposed handles must not open new connections |
| The loop exited due to a **non-CT exception** | CT-cancelled exits are intentional (dispose, manual close) — no retry |
| The tracker is **still reporting `IsActive`** | If the OS already deactivated the device, the normal OS path will re-activate it; no double-reconnect |
| A **delay has elapsed** | Prevents spin-looping against a permanently-failed device or link |

The delay must grow between attempts to avoid hammering a device that is stuck in a
failure loop, but must reset to the initial value on the next OS-driven activation
(i.e., the retry counter resets when `TryOpenDeviceAsync` succeeds).

---

## Decision

After a loop exits with a non-`OperationCanceledException` and `CloseDeviceAsync`
(or `DeactivateAsync` in the non-generic handle) completes, `RunLoopAsync` checks
whether an application-level reconnect should be attempted. If the conditions in
Section 7 are met, it waits for a backoff delay and re-invokes `TryOpenDeviceAsync`
(or `TryActivateAsync`) directly — bypassing the OS event path entirely.

### Backoff schedule

| Attempt | Delay |
|---|---|
| 1 | 1 s |
| 2 | 2 s |
| 3 | 4 s |
| 4+ | 5 s (cap) |

The cap prevents exponential growth from reaching impractical delays for
long-running daemons. The initial 1-second delay gives a faulting port a moment to
recover before the first retry.

### State machine after the fix

```
Loop throws non-CT exception
  │
  ▼
CloseDeviceAsync()           ← IsConnected = false, DeviceClosed fires
  │
  ▼
Disposed?  ──yes──► exit (no reconnect)
  │ no
  ▼
CT cancelled?  ──yes──► exit (intentional close, no reconnect)
  │ no
  ▼
tracker.IsActive?  ──no──► exit (OS path will handle it)
  │ yes
  ▼
await Task.Delay(backoff, ct)
  │
  ▼
TryOpenDeviceAsync()         ← re-enters the normal open path
  │
  ├─ success → IsConnected = true, DeviceOpened fires, loop restarts
  ├─ OpenDeviceAsync throws TException → OpenFailed fires; backoff + retry
  └─ OnConnectedAsync throws → device disposed silently; backoff + retry (no event fired)
```

The reconnect is transparent to consumers — the same `DeviceOpened` / `DeviceClosed`
event pair fires as for any other connect/disconnect cycle. No new API surface is
added.

This decision is now widened slightly: the reconnect state machine must also honour
the original ADR-0027 contract that a loop may **return normally** to trigger close
+ reconnect, and it must retry transient failures that occur during the initial open
/ init-gate phase while the tracker remains active.

### Scope of change

Both handle types are affected:

| Type | Method | Change |
|---|---|---|
| `DeviceProxyBase<TDevice, TException>` | `RunLoopAsync` | Add backoff + conditional `TryOpenDeviceAsync` after `CloseDeviceAsync` |
| `DeviceProxy` (non-generic) | `RunLoopAsync` | Add backoff + conditional `TryActivateAsync` after `DeactivateAsync` |

The `_connectionCts` token is used as the backoff `CancellationToken`. This ensures
that a disposal or an OS-driven deactivation that arrives during the backoff wait
cancels it immediately via `OperationCanceledException`, which the catch block
already ignores correctly.

---

## Consequences

### Positive

- **POS-001**: Fulfils the documented contract — *"reconnect-resilient lifecycle"*
  now covers both OS-driven and application-level failures uniformly.
- **POS-002**: The broken comment in `RunLoopAsync` becomes accurate; no misleading
  documentation remains in the codebase.
- **POS-003**: Zero new API surface — existing consumers gain the behaviour
  automatically without code changes.
- **POS-004**: Consumers writing serial-port or HID loops no longer need to implement
  their own reconnect logic for port-fault scenarios.
- **POS-005**: The exponential backoff avoids resource waste against perpetually-
  broken hardware.
- **POS-006**: Folding the audit findings into the same lifecycle work yields a more
  honest abstraction boundary: consumer notification code and best-effort teardown
  hooks no longer get to decide whether core cleanup happens.

### Negative

- **NEG-001**: A handle whose underlying device link is permanently broken (e.g. a
  Bluetooth device that has been factory-reset and will never reconnect) will retry
  indefinitely at the capped interval. The OS will not report the devnode as inactive
  because the driver is still healthy. Consumers must dispose the handle to stop
  retries; there is no maximum-attempt limit.
- **NEG-002**: The additional retry loop inside `RunLoopAsync` makes the method's
  control flow more complex. The `_disposed` and CT checks must be written carefully
  to avoid races.
- **NEG-003**: Consumers who *want* no retry cannot suppress the behaviour without
  disposing the handle. There is no opt-out flag.
- **NEG-004**: Consumer visibility during retries is limited. `OpenFailed` fires only
  when `OpenDeviceAsync` itself throws; an `OnConnectedAsync` failure (e.g. a protocol
  handshake that times out) is silently swallowed and retried with no event fired.
  There is also no *"currently retrying"* state property or event — a UI that wants
  to display "Reconnecting…" must infer that state from `IsConnected == false` while
  `DeviceInfo` remains non-null (tracker still Active).
- **NEG-005**: The lifecycle implementation becomes more defensive and therefore more
  intricate: CTS cleanup, event isolation, teardown-isolation, startup rollback, and
  retry-on-open all need to cooperate without introducing duplicate close/open work.

---

## Fix Plan

### Phase 1 — Restore lifecycle contract correctness

- **PLN-001**: Change both `RunLoopAsync()` implementations so that **normal loop
  return** follows the same close + reconnect path as non-cancellation exceptions.
- **PLN-002**: Move reconnect orchestration out of the exception-only path and into a
  shared helper that can be invoked after loop return, loop fault, open failure, or
  init-gate failure.

### Phase 2 — Retry initial activation failures

- **PLN-003**: Update `TryOpenDeviceAsync()` and `TryActivateAsync()` so that failed
  open / init-gate attempts trigger the same bounded-backoff reconnect path whenever
  the tracker still reports `IsActive`.
- **PLN-004**: Preserve current notification semantics where possible (`OpenFailed`
  still fires for open failures), while documenting that init-gate failures remain
  silent unless a new notification surface is introduced in a later ADR.

### Phase 3 — Make cleanup exception-safe

- **PLN-005**: Ensure `_connectionCts` is disposed and cleared on every failed
  activation path, not only on successful connected→closed transitions.
- **PLN-006**: Isolate `DeviceOpened` / `DeviceClosed` event handler failures from the
  internal state machine so consumer callbacks cannot prevent loop startup or cleanup.
- **PLN-007**: Isolate `OnDisconnectingAsync()` / `onDeactivated` failures so device
  disposal, CTS cleanup, and state reset still complete.

### Phase 4 — Fix ownership rollback and harden extension hooks

- **PLN-008**: Wrap self-owned `OpenAsync(...)` factories in startup rollback so a
  failing `watcher.StartAsync()` disposes the owned watcher and unsubscribes the handle.
- **PLN-009**: Revisit `DeviceProxyBase<TDevice, TException>` open-failure handling so
  unexpected exception types from `OpenDeviceAsync()` do not fault fire-and-forget
  lifecycle tasks in derived extension packages. This may be implemented as broader
  catch-and-wrap behaviour or by explicitly documenting the stronger override contract.

### Phase 5 — Validate lifecycle behaviour end-to-end

- **PLN-010**: Add focused tests for: normal loop return, loop exception, initial open
  failure with tracker still active, init-gate failure with tracker still active,
  teardown-hook exception, event-handler exception, and watcher-start rollback.
- **PLN-011**: Re-run build and targeted tests before accepting the ADR.

---

## Alternatives Considered

### Application-level heartbeat (`onLoop` responsibility)

- **ALT-001**: **Description**: Consumers write their own retry loop inside `onLoop`,
  catching I/O exceptions, delaying, and re-issuing reads. The handle is not changed.
- **ALT-002**: **Rejection Reason**: Every consumer of every handle type would need
  to implement identical retry logic. This is exactly the boilerplate the handle
  abstraction is meant to eliminate. It also doesn't help consumers using
  `DeviceProxy<TDevice>` (the extension-package shape), where the loop is inside
  the package and consumers cannot modify it.

### `ReconnectAsync()` escape-hatch method

- **ALT-003**: **Description**: Add a public `ReconnectAsync()` method. Consumers
  subscribe to `DeviceClosed` and call it when they determine a retry is appropriate.
- **ALT-004**: **Rejection Reason**: Transfers reasoning about reconnect eligibility
  to consumers, who must understand the tracker's current `IsActive` state, guard
  against double-invocation, and manage backoff themselves. This is the same
  information-leak problem the handle exists to prevent. It also introduces a public
  API that is difficult to constrain safely (e.g. calling `ReconnectAsync` while the
  device is already connected).

### Polling / heartbeat timer internal to the handle

- **ALT-005**: **Description**: Run a background timer inside the handle that
  periodically calls a user-supplied health-check delegate. Trigger close + reconnect
  if the check fails.
- **ALT-006**: **Rejection Reason**: Adds a new timer resource and a new delegate
  parameter to all factory signatures. The health-check concern already belongs to
  `onLoop` — consumers who need periodic probing can issue them there. The existing
  `onLoop` → exception → reconnect path is sufficient once the reconnect half is
  implemented.

---

## Implementation Notes

- **IMP-001**: The retry counter must be local to `RunLoopAsync` and reset to zero
  when `TryOpenDeviceAsync` succeeds. Use a `while` loop that re-runs `OnLoopAsync`
  so that success resets the counter naturally as control falls through to the next
  iteration.
- **IMP-002**: The backoff delay must use `_connectionCts.Token` (or the local `cts`
  snapshot) so that disposal or OS-driven deactivation cancels the wait promptly.
  `OperationCanceledException` from the delay is already handled by the existing CT
  check in the catch block.
- **IMP-003**: The `_disposed` field check inside `RunLoopAsync` must read the same
  field that `DisposeAsync` sets, without taking `_openLock` (which `CloseDeviceAsync`
  may already hold). `_disposed` is written exactly once and read without a lock in
  other places in both handle types — the same pattern is acceptable here.
- **IMP-004**: The non-generic `DeviceProxy.RunLoopAsync` should be brought to
  feature parity with the base-class version in the same commit to avoid a window
  where the two types behave differently.
- **IMP-005**: XML documentation on `OnLoopAsync` (both the abstract definition and
  the delegate parameter in factory methods) should be updated to note that returning
  or throwing triggers close followed by an automatic reconnect attempt if the tracker
  is still active.
- **IMP-006**: The implementation should converge on a single internal helper for
  *"close and maybe reconnect"* rather than maintaining separate logic for loop fault,
  loop return, and initial open failure. That reduces divergence risk between the
  generic and non-generic handle types.
- **IMP-007**: `CloseDeviceAsync()` / `DeactivateAsync()` should become cleanup-first:
  state transition, teardown hook isolation, device disposal, CTS cleanup, and only
  then allow notification exceptions to surface (if they are allowed to surface at all).
- **IMP-008**: Self-owned factory startup should use rollback semantics (`try` / `catch`
  around `watcher.StartAsync()`) so partially-constructed handles do not leak watchers.
- **IMP-009**: Tests are required before accepting this ADR because several of the bugs
  are race- or exception-ordering-sensitive and are easy to regress silently.

---

## References

- **REF-001**: ADR-0027 — `DeviceProxyBase` lifecycle design (the original reconnect
  contract, `onLoop` delegate, per-connection `CancellationToken`).
- **REF-002**: ADR-0028 — `Periphery.Serial` extension (the primary use case that
  surfaced this gap: serial port faults while the USB adapter remains enumerated).
- **REF-003**: `DeviceProxyBase.RunLoopAsync` — the broken comment (`"close + reconnect"`)
  that describes the intended-but-unimplemented behaviour.
