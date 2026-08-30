# Heartbeat and Session Supervision Guide

## Purpose

This guide explains where heartbeat, liveness checks, polling, and session
health policy belong in a Periphery-managed integration.

The short answer is:

**Heartbeat is usually an application/session policy, not a transport primitive.**

This guide generalizes beyond any one protocol or transport.

It is written to keep Periphery and CallAndResponse aligned as they evolve in
parallel:

- Periphery owns lifecycle and reconnect windows.
- CallAndResponse owns framed communication over an active session.
- supervision policy belongs above both.

---

## The core distinction

People often use the word "heartbeat" to describe two different things:

1. **Readiness probe** — "Can this session be considered usable right now?"
2. **Ongoing liveness supervision** — "Has this active session remained healthy over time?"

These belong in different places.

This distinction matters because placing heartbeat logic in the wrong layer
leads to muddy architecture — readiness probes belong in `OnActivatedAsync`,
while ongoing supervision belongs above the protocol layer.

---

## 1. Readiness probe

A readiness probe belongs in the **init gate**.

In Periphery terms, that usually means:

- `OnActivatedAsync`, or
- `onActivated`

Examples:
- send a wake-up sequence,
- verify a firmware version,
- read a status register,
- confirm the far endpoint is responding,
- verify capability/identity before declaring the session connected.

### Why here?
Because readiness determines whether `IsOpen` should become true.

If the probe fails, the session should not be declared usable.

### Good fit
- handshake
- version check
- device identity validation
- one-shot startup command

### Bad fit
- periodic health polling forever
- background telemetry collection
- ongoing keepalive timers

---

## 2. Ongoing liveness supervision

Ongoing heartbeat belongs in the **application/session orchestration layer**.

Examples:
- poll a status register every 5 seconds,
- send a keepalive command periodically,
- verify that a bridge, radio link, or remote endpoint is still responsive,
- mark the session unhealthy after repeated timeouts,
- emit metrics or alarms based on missed beats.

### Why here?
Because these are operational policies, not transport fundamentals.

Heartbeat policy needs to decide:
- interval,
- retry behavior,
- allowed misses,
- escalation rules,
- whether to log, degrade, or fail,
- whether to trigger reconnect or simply report unhealthy state.

Those decisions belong to application/session supervision.

ADR-0030 in Periphery is especially relevant here: Periphery’s reconnect logic
should remain the owner of reconnect. Supervision may determine that a session
is bad enough to let it fail, but it should not compete with the lifecycle owner
for direct control of reopen/close behavior.

---

## Layer placement summary

| Concern | Recommended layer |
|---|---|
| Connect-time readiness probe | `OnActivatedAsync` / activation init gate |
| Ongoing heartbeat / liveness checks | Application/session supervision layer |
| Raw byte read/write | Transport adapter layer |
| Framing | Communication layer |
| Protocol parsing/validation | Protocol client layer |
| Reconnect ownership | Periphery lifecycle layer |

---

## Why heartbeat does not belong in Periphery core

Periphery owns:
- discovery,
- activation/deactivation,
- reconnect windows,
- resource lifetime.

It should not own:
- protocol-specific liveness semantics,
- application-specific health thresholds,
- polling schedules,
- business escalation behavior.

A generic lifecycle library cannot know whether:
- one missed response is fine,
- three missed responses are fatal,
- a given command is a valid heartbeat for your protocol,
- heartbeat should even exist for a given session.

Those are policy decisions outside Periphery's responsibility.

Periphery’s `DeviceProxyBase` gives you the timing hooks and cancellation model
you need. It should not become a hidden policy engine.

---

## Why heartbeat does not belong in the raw transport adapter

A raw byte transport adapter should only know how to move bytes.

If heartbeat is placed there, the adapter becomes aware of:
- protocol framing,
- semantic commands,
- health meaning,
- scheduling.

That is a layering violation.

A serial adapter should not know what constitutes a valid Modbus heartbeat.
A HID adapter should not know what a vendor keepalive command means.
A BLE byte source should not decide session health.

---

## Why heartbeat should usually not be hardcoded into protocol clients

A protocol client should expose semantic operations.

It is acceptable for a protocol client to expose an operation that *can be used*
as a heartbeat, such as:
- `ReadStatusAsync()`
- `PingAsync()`
- `ReadIdentityAsync()`

But the protocol client should generally not:
- schedule heartbeat on its own,
- declare the session healthy/unhealthy globally,
- trigger reconnect independently,
- own background timers by default.

Those are orchestration concerns.

This is consistent with the direction of ADR-0011: protocol-facing abstractions
should answer communication questions, not lifecycle or supervision questions.

---

## Recommended supervision model

Use a **session supervisor** in the application/session layer.

A session supervisor is responsible for:
- running background heartbeat tasks,
- interpreting repeated failures,
- updating health state,
- optionally surfacing metrics or events,
- respecting the same request-serialization rules as normal traffic.

This allows supervision policy to evolve independently from:
- Periphery lifecycle code,
- communication code,
- protocol client code.

A good mental model is:

- Periphery creates the active session window.
- the session supervisor watches and interprets that window.
- the communication layer just exchanges messages inside it.

---

## Single-exchange rule

Heartbeat traffic must obey the same exchange-serialization rule as normal work.

If a session only supports one in-flight request/response at a time, then:
- heartbeat must acquire the same session gate,
- application requests must acquire the same session gate,
- no background keepalive should bypass that gate.

Otherwise you risk:
- interleaved request/response traffic,
- broken framing assumptions,
- corrupted protocol state,
- impossible-to-debug race conditions.

This rule is non-negotiable unless you have deliberately designed a multiplexed
session model.

---

## Recommended shapes

## Shape A — Readiness probe in init gate

Use this when the check determines whether the session should be considered
connected at all.

Examples:
- initial status read,
- hello/ack exchange,
- identity/version check,
- wake-up command.

Good properties:
- semantically honest `IsOpen`,
- failure prevents premature publication of a bad session,
- no long-running task required.

---

## Shape B — Background heartbeat supervisor

Use this when the session should remain available to callers while also being
monitored over time.

Supervisor responsibilities:
- run until the session is cancelled,
- acquire the session request gate,
- perform heartbeat operations at the configured interval,
- record health state,
- optionally fail the session after repeated faults.

Good properties:
- policy is explicit,
- normal requests and heartbeat coexist cleanly,
- no protocol or transport pollution.

This is the default recommendation for most shared-service scenarios.

---

## Shape C — Dedicated polling worker

Use this when the device exists primarily to be polled or streamed
autonomously rather than called on-demand by many services.

Examples:
- telemetry collector,
- barcode scanner read loop,
- dedicated sensor ingestion worker.

In this case the `createSession` delegate of `DeviceSessionHost` is the right
place to set up polling workers that run for the lifetime of the session.

This is appropriate when the handle's purpose is already specialized and not
meant to publish a shared request/response client to the rest of the app.

---

## Health semantics to decide explicitly

Any heartbeat design should answer these questions explicitly:

### 1. What operation counts as a heartbeat?
Examples:
- read status register,
- ping opcode,
- no-op command,
- identity read,
- vendor keepalive.

### 2. How often should it run?
- fixed interval,
- jittered interval,
- idle-only interval,
- adaptive interval.

### 3. What counts as failure?
- timeout,
- transport exception,
- protocol exception,
- semantic error payload,
- N consecutive misses.

### 4. What should happen after failure?
- just log,
- mark unhealthy,
- stop publishing the session,
- throw from the supervisor,
- trigger reconnect indirectly by failing the active loop.

### 5. Should callers still be allowed to use the session while unhealthy?
Possible policies:
- yes, best effort,
- no, fail fast,
- yes until threshold exceeded.

These are all application-level policy choices.

If they are not written down, the architecture usually drifts toward accidental
behavior.

---

## Interaction with reconnect

Heartbeat should not usually own reconnect directly.

Instead:
- the supervisor detects a severe enough failure,
- the supervisor throws or otherwise ends the active session,
- the Periphery handle observes loop exit/failure and performs the reconnect
  behavior it already owns.

This preserves a single clear lifecycle owner.

### Good pattern
- supervisor fault -> active session ends -> Periphery reconnects

### Bad pattern
- supervisor directly opens/closes underlying transport while the handle also
  believes it owns lifecycle

This principle matters even more in light of ADR-0030: Periphery is already
becoming more explicit about application-level reconnect after silent failures.
Do not build a second reconnect state machine in heartbeat code.

---

## Session publication guidance

If a session is published to the rest of the app, heartbeat should be treated as
one consumer of that session, not its owner.

That means:
- heartbeat uses the same session object as everyone else,
- heartbeat acquires the same synchronization gate,
- heartbeat does not keep a second hidden protocol client,
- heartbeat does not keep its own alternate transport path.

This keeps session state coherent.

---

## When not to add heartbeat at all

Heartbeat is not always required.

Do not add heartbeat just because "it feels safer" unless you actually need one
of these outcomes:

- keep a link awake,
- detect silent failures faster than organic traffic would,
- surface explicit health status,
- satisfy a device/protocol requirement,
- maintain radio or bridge state.

If the application naturally sends frequent real traffic, that traffic may
already be sufficient to demonstrate liveness.

Unnecessary heartbeat adds:
- bus traffic,
- contention with normal operations,
- failure modes,
- architectural complexity.

---

## Anti-patterns to avoid

### 1. Heartbeat in Periphery core
Periphery should not become a protocol-policy framework.

### 2. Heartbeat inside raw byte adapters
Byte adapters should not know protocol meaning.

### 3. Hidden background heartbeat inside protocol clients
This hides policy and creates surprising behavior.

### 4. Heartbeat bypassing the session gate
This creates overlapping exchanges and race conditions.

### 5. Separate hidden transport for heartbeat
All session supervision should use the same active session model as normal work.

### 6. Double reconnect ownership
Heartbeat should not directly fight with the lifecycle owner over reconnect.

### 7. Making readiness and liveness indistinguishable
A one-shot init probe and a long-running liveness policy are not the same thing.
Treating them as one concept usually causes logic to migrate into the wrong hook.

---

## Recommended default

Use this default unless your scenario strongly suggests otherwise:

1. Put a one-shot readiness probe in `OnActivatedAsync` / `onActivated` if needed.
2. Use `DeviceSessionHost` to publish a session object once the device is ready.
3. Run any ongoing heartbeat in a session supervisor above the protocol layer.
4. Route heartbeat traffic through the same session gate as all other requests.
5. If heartbeat determines the session is irrecoverably bad, let the active
   session fail so the Periphery handle can reconnect.

---

## Final principle

**Readiness belongs to connection establishment.  
Liveness belongs to session supervision.  
Reconnect belongs to the lifecycle owner.**