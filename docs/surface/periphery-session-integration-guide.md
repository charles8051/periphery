# Periphery Session Integration Guide

## Purpose

This guide defines the recommended architecture for integrating a
Periphery-managed device/session lifecycle with a higher-level communication
stack such as CallAndResponse or any protocol/application client built on top of
an active byte-oriented connection.

This guide is intentionally general:

- the underlying transport may be serial, HID, BLE, USB, or another byte-capable
  resource,
- the higher-level client may be Modbus, a bootloader client, a proprietary
  protocol, or an application-specific command client,
- the stable architecture is the same regardless of transport or protocol.

Periphery is explicitly a **discovery/lifecycle** foundation, not a protocol or
I/O-policy framework. Its `DeviceProxyBase`, `DeviceProxy<TDevice>`, and
non-generic `DeviceProxy` types define the active-session window through
`OnActivatedAsync`. `DeviceSessionHost` publishes a typed session object once
activation succeeds, making Periphery the natural lifecycle owner for any
companion I/O package or application integration. Likewise,
CallAndResponse is moving toward a composition model where a raw byte source is
wrapped into framed communication behavior via `IByteSource` and
`Transceiver.Wrap(...)`, leaving protocol clients transport-agnostic. This guide
documents how those two directions should meet cleanly. 

---

## Core Principle

**Periphery owns lifecycle. Communication layers own message exchange. Protocol and application layers own semantics and policy.**

In practice:

- `Periphery` owns discovery, activation, deactivation, reconnect, and the
  active-session execution window.
- a transport adapter owns raw byte I/O against an already-open resource.
- a communication layer owns framing, accumulation, and request/response helpers.
- a protocol client owns semantic operations over that communication layer.
- application orchestration owns heartbeat, polling, health policy, retries,
  and cross-service exposure.

This keeps lifecycle ownership singular and avoids ambiguity about who is
allowed to open, close, reconnect, or declare a session healthy.

---

## Why this architecture exists

The Periphery and CallAndResponse design directions now meet naturally:

1. Periphery opens and manages the underlying resource lifecycle.
2. That active resource exposes raw byte I/O.
3. A communication layer wraps those bytes into framed exchange semantics.
4. A protocol/application client consumes that framed communication.
5. Application code decides how to supervise and expose the session.

This pattern avoids:

- dual ownership of open/close,
- inheritance conflicts between lifecycle bases and communication bases,
- protocol code that knows too much about hardware,
- and transport code that knows too much about business behavior.

It also lines up with the current ADR tension in CallAndResponse:

- **ADR-0010** keeps lifecycle members on `ITransceiver`, but allows wrapped
  sources whose lifecycle is externally managed and whose `Open`/`Close` become
  no-ops.
- **ADR-0011** argues that lifecycle ownership should be removed from
  `ITransceiver` entirely because the transceiver’s real job is framed
  communication over an already-available session.

Whichever direction ultimately wins, the architectural boundary remains the same:
**Periphery is the lifecycle owner; the communication layer is the message-layer
consumer of that session.**

---

## Layer Model

## 1. Periphery lifecycle layer

This layer owns:

- discovery and matching,
- connect/disconnect/reconnect,
- per-connection cancellation,
- active-session lifetime,
- resource open/close.

Typical Periphery surfaces:

- `DeviceProxyBase<TDevice, TException>`
- `DeviceProxy<TDevice>`
- non-generic `DeviceProxy`
- `OnActivatedAsync`
- `OnDeactivatedAsync`
- `DeviceSessionHost<TSession>`

### Put here
- opening a port/device/pipe/connection
- init-gate validation before declaring the session usable
- disposing the underlying resource
- reacting to device disappearance or reconnect

### Do not put here
- protocol parsing
- framed message policy
- business workflows
- long-term application retry semantics
- heartbeat as an application policy

Periphery is the **session owner**, not the protocol host.

---

## 2. Raw transport adapter layer

This layer adapts the active resource into a narrow byte I/O surface.

Examples:
- serial port read/write adapter
- HID report adapter
- BLE UART adapter
- USB bulk endpoint adapter
- stream/socket adapter

Responsibilities:
- expose raw reads and writes,
- report whether the resource is still usable,
- honor cancellation,
- remain thin.

This layer should not decide:
- when to reconnect,
- whether the session is healthy,
- what messages mean,
- whether a request should be retried.

If integrating with CallAndResponse, this is the seam represented by
`IByteSource`. ADR-0010 explicitly identifies those six primitives as the right
public bridge from an externally managed transport into the framing engine.

---

## 3. Framing / communication layer

This layer turns raw bytes into reusable communication primitives.

If using CallAndResponse, this is the layer represented by:

- `IByteSource`
- `Transceiver.Wrap(IByteSource)`
- `ITransceiver` / `Transceiver`
- `SendReceive*` convenience methods

Responsibilities:
- accumulation,
- frame/message detection,
- request/response convenience,
- transport-fault propagation.

It should not own:
- hardware session lifecycle,
- reconnect policy,
- heartbeat policy,
- or application semantics.

This is the **communication primitive** layer.

---

## 4. Protocol or command client layer

This layer turns framed communication into semantic operations.

Examples:
- Modbus client
- STM32 bootloader client
- proprietary command client
- line-oriented command gateway
- binary control-plane client

Responsibilities:
- construct semantic requests,
- validate and parse semantic responses,
- raise protocol-specific errors,
- expose domain-friendly operations.

It should not own:
- transport lifecycle,
- reconnect behavior,
- background supervision,
- global health state.

A protocol client should be viewed as a **session-scoped adapter over an already-active communication primitive**.

---

## 5. Application/session orchestration layer

This is the correct home for:

- exposing reusable operations to the rest of the app,
- serializing access across callers,
- background polling,
- heartbeat,
- health interpretation,
- disconnected behavior,
- retry policy,
- session publication and withdrawal.

If another service in the application needs to "talk to the device," it should
usually talk to this layer rather than to the handle or raw transport directly.

This is also where the current Periphery reconnect story and the current
CallAndResponse composition story become operationally useful. Periphery gives
you the active-session lifetime. CallAndResponse gives you the framing engine.
This layer decides what the application does with that active session.

---

## Stable composition pattern

The recommended composition is:

1. Periphery opens the underlying resource.
2. The resource is adapted to a raw byte I/O abstraction.
3. A communication wrapper is created for that active resource.
4. A protocol/application client is created for that active session.
5. A session object is published to the rest of the application.
6. Other services call through an application-facing service.
7. Disconnect unpublishes the session immediately.

This pattern remains valid even if API details evolve over time.

---

## Recommended public API shape

The two-level model this guide originally described — `DeviceSessionHost<TSession>`
as the lifecycle primitive plus `DeviceFacade<TSession>` as a lower-ceremony facade
over it — collapsed to one level. `DeviceFacade<TSession>` and `DeviceUseResult`
were removed; [ADR-0033](../adr/0033-device-facade.md) is Superseded. The facade's
whole job was fail-fast access to the current session, and the host already does
that, so the second type earned nothing.

`DeviceSessionHost<TSession>` is the single integration point:

| You want | Use |
|---|---|
| The session, or a throw if there isn't one | `GetRequiredSession()` |
| A non-throwing availability check | `TryGetCurrentSession(out var session)` |
| To wait for the next session | `await WaitForSessionAsync(ct)` |
| To inspect without acquiring | `HasSession`, `CurrentSession`, `ConnectionState`, `Status` |
| To react to turnover | `StatusChanged`, `PropertyChanged` |

### Rule of thumb

Build a **typed client** over the host — `ModbusClient`, `ScannerClient` — so callers
say "perform this operation against the device" and never touch `DeviceSessionHost`
directly. The client owns the protocol; the host owns the lifecycle. That is the same
shape the facade was reaching for, minus a type.

### Concurrency placement

`DeviceSessionHost<TSession>` does not enforce global serialization.

If a protocol requires single-flight request/response behavior, put the
`SemaphoreSlim` (or equivalent gate) in the typed protocol client or session
wrapper, not in the host.

---

## Session scope vs application scope

### Session-scoped objects
Create these per active connection:

- transport adapters,
- wrapped transceivers / communication objects,
- protocol clients,
- session-specific supervisors,
- per-connection synchronization gates.

### Application-scoped objects
These may live for the process lifetime:

- the Periphery handle,
- a host that publishes the current session,
- application-facing service classes,
- configuration and policy objects.

### Rule of thumb

If an object directly depends on the active connection still being alive, it is
probably **session-scoped**, not singleton-scoped.

This is especially important for higher-level clients. A `ModbusRtuClient`,
bootloader client, or proprietary command client should usually be treated as a
**view over the current session**, not a permanent transport owner.

---

## The role of `OnActivatedAsync`

`OnActivatedAsync` is the **init gate**.

Use it for work that must succeed before the session is considered usable.

Examples:
- handshake,
- wake-up command,
- firmware/version check,
- capability discovery,
- initial readiness probe.

If this work fails, the session should not be considered connected.

Do not use `OnActivatedAsync` for long-running background work.

This aligns exactly with Periphery’s lifecycle contract: `OnActivatedAsync`
runs inside the open lock, before `IsOpen` becomes true. That is the right
place for *readiness*, not for *operations over time*.

---

## The role of `DeviceSessionHost`

`DeviceSessionHost<TSession>` is the **active-session publication point**.

Use it to:
- create session-scoped communication objects via `createSession`,
- publish active-session availability via `HasSession` and `WaitForSessionAsync`,
- optionally run session supervision in a background task started from `createSession`,
- await the session’s end via the `CancellationToken` passed to `createSession`.

For shared application services, the session object created by `createSession`
is what exposes availability to the rest of the application — do not route all
requests through the lifecycle layer itself.

This keeps business behavior decoupled from the reconnect state machine:
`createSession` creates the session, `onSessionEnded` tears it down, and the
reconnect policy is owned entirely by `DeviceSessionHost`.

---

## Concurrency rule

For most request/response protocols over a single connection, assume:

**one in-flight exchange at a time per active session**

Even if the transport can move bytes concurrently, the higher-level framing and
protocol semantics usually require serialization.

Therefore:
- session publication can be simple,
- actual exchange methods should usually be serialized by a session object or
  application-facing service.

If you later need pipelining or multiplexing, design it deliberately.

This rule matters even more once you add heartbeat or background supervision:
those activities must use the same gate as regular requests unless your
protocol/transport has been explicitly designed for overlap.

---

## Disconnected behavior options

Application-facing services should choose one clear semantic when no session is
active.

### Option A — Fail fast
If there is no active session, fail immediately.

Use when:
- the device is expected to be available,
- absence is exceptional,
- callers should choose their own retry behavior.

### Option B — Wait for session
Wait until a new session becomes active before issuing work.

Use when:
- reconnect is expected and normal,
- "wait until available" is a useful behavior,
- you are willing to manage more complexity.

Start with **fail fast** unless your scenario clearly benefits from queued or
awaited work.

---

## Anti-patterns to avoid

### 1. Dual lifecycle ownership
Do not let both the session host and the communication/client layer believe they
own `open` / `close`.

### 2. Singleton protocol client over ephemeral transport
Do not treat a session-bound client as globally valid if the underlying session
can come and go.

### 3. Raw transport leakage
Do not expose raw `SerialPort`, HID handles, BLE writers, or similar primitives
throughout the app.

### 4. Policy in the wrong layer
Do not put heartbeat, health policy, or business retry behavior into Periphery
core or raw transport adapters.

### 5. Overlapping exchanges
Do not let multiple callers, supervisors, and background tasks issue overlapping
request/response exchanges on the same session unless that behavior is explicitly
designed.

### 6. Hiding session lifetime behind “convenient” abstractions
If a service depends on an active device session, that dependency should be
visible in the architecture. Do not pretend a reconnecting device behaves like a
permanently available in-memory service.

---

## Relationship to CallAndResponse

If using CallAndResponse as the communication layer:

- `IByteSource` is the raw-byte seam.
- `Transceiver.Wrap(IByteSource)` is the bridge from an externally managed
  transport to framed communication behavior.
- protocol clients should depend on the communication abstraction, not on the
  lifecycle owner.
- Periphery remains the correct owner of connection lifetime.

There is active architectural tension between:
- keeping lifecycle members on `ITransceiver` but making them no-ops in wrapped
  scenarios, and
- removing lifecycle ownership from `ITransceiver` entirely.

Regardless of which API shape wins, the correct layering remains the same:

- lifecycle/session owner below,
- byte source / communication wrapper in the middle,
- protocol/application client above,
- orchestration and policy at the top.

If you preserve these boundaries, your integrations will survive API evolution
cleanly.

---

## Implementation checklist

When integrating Periphery with a communication/protocol stack, ask:

### Lifecycle
- Is there exactly one clear owner of session lifecycle?
- Does disconnect cancel in-flight work promptly?
- Does reconnect happen at the handle/session layer rather than in protocol code?

### Transport
- Is the raw resource adapted to a narrow byte-I/O surface?
- Is lifecycle kept out of the adapter?
- Does the adapter honor cancellation and transport failure correctly?

### Communication
- Is framing layered on top of an already-active resource?
- Does the communication layer avoid claiming lifecycle ownership?

### Client design
- Are protocol/application clients session-scoped?
- Do they expose semantics rather than transport details?
- Are they decoupled from resource ownership?

### Application service design
- Do other services depend on an application-facing service rather than the raw
  handle or raw transport?
- Is request concurrency serialized?
- Is disconnected behavior explicit?
- Is heartbeat hosted in the application/session layer?

If the answer to all of these is yes, the architecture is likely in the right place.

---

## Recommended default pattern

If you need one safe default, use this:

1. Use a Periphery handle as the sole lifecycle owner.
2. Open the underlying resource in the activation/init phase.
3. Adapt the resource to a narrow byte-I/O abstraction.
4. Build a session-scoped communication primitive over that abstraction.
5. Build a session-scoped protocol/application client over that primitive.
6. Publish the current session through a host/service boundary.
7. Let other application services call through that boundary.
8. Keep heartbeat and liveness policy in the application/session layer.

---

## Final principle

**A device/session handle should answer "is the resource alive and available?"  
A communication primitive should answer "can bytes/messages be exchanged?"  
A protocol/application client should answer "what does this exchange mean?"  
Do not make one layer answer all three questions.**
