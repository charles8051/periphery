---
title: "ADR-0033: DeviceFacade — Lower-Ceremony Consumer API over DeviceSessionHost"
status: "Superseded"
date: "2026-03-25"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "lifecycle", "session", "facade", "api", "httpclient"]
supersedes: ""
superseded_by: "Removed — DeviceFacade<TSession> and DeviceUseResult deleted; consumers use DeviceSessionHost<TSession> directly"
---

# ADR-0033: DeviceFacade — Lower-Ceremony Consumer API over DeviceSessionHost

## Status

**Superseded** — `DeviceFacade<TSession>` and `DeviceUseResult` have been removed from the library. Consumers use `DeviceSessionHost<TSession>` directly via `TryGetCurrentSession` / `GetRequiredSession`.

---

## Context

### 1. ADR-0032 fixed the lifecycle abstraction, but not the final consumer experience

ADR-0032 introduces `DeviceSessionHost<TSession>` as the first-class Periphery type for:

- session creation,
- session publication,
- disconnect withdrawal,
- reconnect,
- and status observation.

That is the correct lifecycle boundary. It removes repeated boilerplate and fixes the
two-phase initialization defect found in hand-rolled application hosts.

However, a top-level consumer still needs to think in terms of:

- `GetRequiredSession()`,
- `WaitForSessionAsync()`,
- or `HostStatus<TSession>`.

That is a major improvement over manual `DeviceProxy` composition, but it is still
ceremonial for the most common application case: *"I have a long-lived client-like
object. I want to call an operation against the currently active device when possible."*

### 2. `HttpClient` shows the right *shape* of API

`HttpClient` is not a socket. It is a stable facade over lower-level connection behavior.
Its consumer does not explicitly:

- acquire a socket,
- hold a current connection object,
- or manually react to connection pooling and rotation.

Instead, the user gets a long-lived object and calls:

```csharp
await httpClient.SendAsync(request, cancellationToken);
```

The facade hides transport-level churn while preserving explicit failure semantics.

This is good inspiration for Periphery's consumer experience:

- the user should depend on a stable object,
- the common path should be "invoke the operation,"
- and the lower-level session should usually be an implementation detail.

### 3. But physical-device absence is not the same as socket churn

Periphery cannot copy `HttpClient` blindly.

Unlike a pooled network socket, a physical device may be:

- unplugged,
- not yet present in the device tree,
- enumerated but still starting,
- or repeatedly failing session creation.

These are first-class states in Periphery's model. Hiding them completely would produce
an API that looks simple but lies about system reality.

Therefore the goal is **not** "make device state disappear."

The goal is:

- hide session-acquisition mechanics from the common call path,
- while preserving explicit status/health surfaces for UI, telemetry, and diagnostics.

### 4. The current API exposes the session too early for many consumers

`DeviceSessionHost<TSession>` is appropriate when application code genuinely needs:

- direct session access,
- explicit pattern matching on lifecycle state,
- or custom coordination over the session object itself.

But many consumers do not care about the session object. They care about operations:

- "read holding registers,"
- "query firmware version,"
- "write configuration block,"
- "reset the device."

For those callers, exposing `TSession` directly is similar to requiring `HttpClient`
consumers to ask for a pooled `Socket` before each request. It is the wrong level of
abstraction for the happy path.

### 5. Periphery should offer both the lifecycle primitive and the operation facade

The architectural layering from the patterns guide still applies:

- Periphery owns lifecycle.
- Communication layers own message exchange.
- Protocol/application layers own semantics and policy.

The new question is not whether to replace `DeviceSessionHost<TSession>`, but whether
Periphery should provide a **second, higher-level facade** for the operation-oriented
consumer experience.

This ADR answers yes.

---

## Decision

Introduce a second public abstraction above `DeviceSessionHost<TSession>`:

- `DeviceSessionHost<TSession>` remains the lifecycle primitive.
- `DeviceFacade<TSession>` becomes the stable operation facade for most consumers.

`DeviceFacade<TSession>` is inspired by `HttpClient` in *shape*, not in detailed
semantics. It provides a long-lived object with invocation methods that operate against
the currently active session when available, while leaving device absence and reconnect
visible through explicit status APIs.

---

### DEC-001: `DeviceSessionHost<TSession>` remains the lifecycle owner

This ADR does **not** replace ADR-0032.

`DeviceSessionHost<TSession>` remains the correct primitive for:

- device discovery,
- activation/deactivation,
- reconnect,
- session creation and withdrawal,
- and `HostStatus<TSession>`.

The new facade composes it.

Conceptually:

```csharp
public sealed class DeviceFacade<TSession>
    where TSession : class
{
    private readonly DeviceSessionHost<TSession> _host;
}
```

### DEC-002: Add `DeviceFacade<TSession>` as the common consumer-facing facade

The facade is operation-oriented. It does not require the caller to retrieve a session
first in the common path.

Proposed shape:

```csharp
public sealed class DeviceFacade<TSession> : IAsyncDisposable
    where TSession : class
{
    public static async Task<DeviceFacade<TSession>> OpenAsync(
        DeviceProfile profile,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        CancellationToken ct = default);

    public static DeviceFacade<TSession> Create(
        DeviceSessionHost<TSession> host);

    public HostStatus<TSession> Status { get; }
    public bool IsConnected { get; }
    public DeviceInfo? DeviceInfo { get; }

    public Task UseAsync(
        Func<TSession, CancellationToken, Task> action,
        CancellationToken ct = default);

    public Task<TResult> UseAsync<TResult>(
        Func<TSession, CancellationToken, Task<TResult>> action,
        CancellationToken ct = default);

    public Task<DeviceUseResult> TryUseAsync(
        Func<TSession, CancellationToken, Task> action,
        CancellationToken ct = default);

    public Task<DeviceUseResult<TResult>> TryUseAsync<TResult>(
        Func<TSession, CancellationToken, Task<TResult>> action,
        CancellationToken ct = default);

    public ValueTask DisposeAsync();
}

public readonly record struct DeviceUseResult(
    bool Success,
    HostStatusKind StatusKind);

public readonly record struct DeviceUseResult<TResult>(
    bool Success,
    TResult? Result,
    HostStatusKind StatusKind);

public enum HostStatusKind
{
    DeviceAbsent,
    SessionStarting,
    SessionActive,
    SessionUnavailable
}
```

This produces the intended experience:

```csharp
ushort[] registers = await modbusClient.UseAsync(
    (session, ct) => session.ReadHoldingRegistersAsync(1, 0, 2, ct),
    cancellationToken);
```

The consumer does not need to:

- call `GetRequiredSession()`,
- manage a nullable current session,
- or understand how reconnect is implemented.

### DEC-003: Invocation methods are the primary happy path, but they fail fast on unavailability

For operation-oriented consumers, the preferred API is invocation:

- `UseAsync(...)` — invoke immediately when active; otherwise fail fast
- `TryUseAsync(...)` — do not throw for unavailability; return an unsuccessful result

This mirrors the `HttpClient` mental model:

- the user holds a long-lived client object,
- each operation is expressed as a single call,
- transport/session mechanics stay beneath the call boundary.

Suggested semantics:

#### `UseAsync(...)`

- If `Status` is `SessionActive<TSession>`, invoke immediately.
- Otherwise fail immediately with an exception describing the current host status.

This aligns the facade more closely with `HttpClient`: the caller gets a simple call
surface, but lack of connectivity/device availability is surfaced as an immediate call
failure rather than an implicit wait.

#### `TryUseAsync(...)`

- If `Status` is `SessionActive<TSession>`, invoke immediately and return success.
- Otherwise return an unsuccessful `DeviceUseResult` / `DeviceUseResult<TResult>`
  containing the current status kind without waiting.

This makes UI and polling scenarios straightforward.

### DEC-004: Status remains explicit and first-class

The facade should hide **session-acquisition ceremony**, not **device state**.

Therefore `DeviceFacade<TSession>` forwards:

- `Status`,
- `IsConnected`,
- and `DeviceInfo`.

Consumers that need richer behavior can still branch on:

```csharp
switch (client.Status)
{
    case SessionActive<MySession>:
    case SessionStarting<MySession>:
    case SessionUnavailable<MySession>:
    case DeviceAbsent<MySession>:
}
```

This preserves observability for:

- UI availability indicators,
- health reporting,
- retry dashboards,
- and diagnostics.

### DEC-005: Exceptions should describe operation failure, not session plumbing

When `UseAsync(...)` is called while no session is active, it should fail immediately
with an exception that reflects current unavailability rather than waiting for a future
session.

When the caller cancels an already-running operation, it throws `OperationCanceledException`.

When the supplied operation throws, that exception propagates unchanged.

The facade should not introduce automatic replay or wrapper exceptions for normal
operation failures.

If a caller wants non-throwing behavior for unavailability, `TryUseAsync(...)` is the
non-exceptional path.

This follows the `HttpClient` lesson that the call surface should primarily talk in
terms of the operation being attempted, not internal connection management details.

### DEC-006: Typed protocol/application clients should build on `DeviceFacade<TSession>`

For the cleanest experience, application or protocol packages should typically expose a
typed facade that wraps `DeviceFacade<TSession>`, not `DeviceSessionHost<TSession>`
directly.

Sketch:

```csharp
public sealed class ModbusClient : IAsyncDisposable
{
    private readonly DeviceFacade<ActiveModbusSession> _client;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private ModbusClient(DeviceFacade<ActiveModbusSession> client)
    {
        _client = client;
    }

    public static async Task<ModbusClient> OpenAsync(
        DeviceProfile profile,
        CancellationToken ct = default)
    {
        var client = await DeviceFacade<ActiveModbusSession>.OpenAsync(
            profile,
            createSession: (device, ct) => BuildModbusSessionAsync(device, ct),
            onSessionEnded: s => DisposeModbusSessionAsync(s),
            ct: ct).ConfigureAwait(false);

        return new ModbusClient(client);
    }

    public HostStatus<ActiveModbusSession> Status => _client.Status;

    public Task<ushort[]> ReadHoldingRegistersAsync(
        byte unitIdentifier,
        ushort startingAddress,
        ushort numberOfRegisters,
        CancellationToken ct = default)
    {
        return UseSerializedAsync(
            (session, ct) => session.ReadHoldingRegistersAsync(
                unitIdentifier,
                startingAddress,
                numberOfRegisters,
                ct),
            ct);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<TResult> UseSerializedAsync<TResult>(
        Func<ActiveModbusSession, CancellationToken, Task<TResult>> action,
        CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _client.UseAsync(action, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
```

This is the closest Periphery analogue to `HttpClient`:

- long-lived object,
- method calls for operations,
- internal connection/session churn hidden,
- status still observable when needed.

### DEC-007: `DeviceSessionHost<TSession>` stays public for advanced scenarios

`DeviceFacade<TSession>` is the recommended default for common operation-oriented use,
but `DeviceSessionHost<TSession>` remains public and first-class for advanced scenarios:

- application code that must hold the session directly,
- actor/supervisor models,
- multi-session coordination,
- explicit orchestration around session turnover,
- or APIs that intentionally expose the session abstraction.

This avoids forcing all consumers into one model.

### DEC-008: `Create(host)` is non-owning by default

`DeviceFacade<TSession>.Create(host)` wraps an existing
`DeviceSessionHost<TSession>` without taking ownership of its lifecycle unless a
separate explicitly-owning factory is added in the future.

This avoids surprising disposal behavior when callers compose facades around
shared host instances.

### DEC-009: Do not model the top-level API after `HttpClientHandler`

`HttpClient` is useful inspiration at the facade level, but Periphery should not mirror
the full `HttpClient`/handler pipeline architecture.

Reasons:

- Periphery's core concern is lifecycle and device presence, not middleware composition.
- The ecosystem already has natural extension layers: transport adapter, framing layer,
  protocol client, application service.
- A handler pipeline would add complexity without solving the main ceremony problem.

The useful lesson is the stable client facade, not the entire HTTP stack architecture.

---

## Rationale

### 1. It makes the happy path match user intent

Most consumers do not want "a session." They want "an operation against the device."

`DeviceFacade<TSession>.UseAsync(...)` maps directly to that intent while still
failing immediately when no active session exists.

### 2. It preserves truth about physical-device reality

A fully transparent abstraction would be dishonest in a device world. Devices can be
missing, still starting, or reconnecting after a fault.

Forwarding `Status` preserves this truth without burdening the common path.

### 3. It gives Periphery a clean two-level public model

- `DeviceSessionHost<TSession>` = lifecycle primitive
- `DeviceFacade<TSession>` = consumer facade

That split is easy to explain and document.

### 4. It encourages protocol packages to present polished top-level APIs

A serial-backed Modbus package should ideally expose `ModbusClient`, not require every
consumer to understand session hosting. The facade model nudges extensions toward that
cleaner shape.

### 5. It is compatible with existing layering guidance

The facade does not collapse transport, framing, protocol, and application semantics
together. It simply gives the caller a better entry point into that stack.

---

## Alternatives Considered

### ALT-001: Keep `DeviceSessionHost<TSession>` as the only public abstraction

- **Description**: Treat `GetRequiredSession()`, `WaitForSessionAsync()`, and `Status` as
  sufficient for all consumers.
- **Rejection reason**: Correct, but still too ceremonial for the common path. It solves
  lifecycle ownership, but not user ergonomics.

### ALT-002: Hide `Status` entirely behind automatic waiting/retry

- **Description**: Present a fully transparent facade that never surfaces device state.
- **Rejection reason**: This would make the API deceptively simple while hiding crucial
  operational truth. In Periphery, physical absence is not an implementation detail.

### ALT-003: Add extension methods to `DeviceSessionHost<TSession>` instead of a new type

- **Description**: Provide `UseAsync(...)` and `TryUseAsync(...)` as extension methods on
  `DeviceSessionHost<TSession>`.
- **Rejection reason**: Better than nothing, but weaker for discoverability, DI
  registration, and conceptual clarity. A named `DeviceFacade<TSession>` communicates the
  intended consumer role more clearly.

### ALT-004: Make `DeviceFacade<TSession>` non-generic

- **Description**: Erase the session type from the facade entirely.
- **Rejection reason**: The operation delegate fundamentally needs the session type, and
  typed protocol/application wrappers are easier to build when the underlying facade is
  generic.

### ALT-005: Model Periphery after the full `HttpClient`/handler pipeline

- **Description**: Introduce handler chains or middleware around device operations.
- **Rejection reason**: Overfits the analogy. Periphery needs a stable facade, not a full
  HTTP-style extensibility subsystem.

---

## Implementation Notes

- **IMP-001**: `DeviceFacade<TSession>.OpenAsync(...)` can internally create a
  `DeviceSessionHost<TSession>` and wrap it. `Create(host)` should allow reusing an
  existing host where the caller already owns lifecycle setup.

- **IMP-002**: `UseAsync(...)` can be implemented in terms of
  an immediate `Status is SessionActive<TSession>` check, followed by delegate
  invocation. It should not wait for future availability implicitly.

- **IMP-003**: `TryUseAsync(...)` should use the host's immediate state check
  (`Status is SessionActive<TSession>`) and avoid waiting.

- **IMP-004**: The facade should not cache a session reference between operations.
  Each call should resolve against the current host state so that reconnect/session
  turnover is naturally handled.

- **IMP-005**: `Create(host)` is non-owning. `DisposeAsync()` should delegate to the
  underlying host only when the facade created and owns it internally.

- **IMP-006**: Pattern documentation should show:
  - advanced examples using `DeviceSessionHost<TSession>`,
  - and top-level consumer examples using `DeviceFacade<TSession>` or typed wrappers such
    as `ModbusClient`.

- **IMP-007**: `TryUseAsync<TResult>` should return a dedicated result type
  (`DeviceUseResult<TResult>`) rather than a tuple so unavailability can be represented
  explicitly without overloading `default`.

- **IMP-008**: `DeviceFacade<TSession>` should not enforce global serialization itself.
  If a protocol requires single-flight access, typed wrappers such as `ModbusClient`
  should coordinate that with `SemaphoreSlim` (or equivalent) at the protocol/client
  layer.

---

## Consequences

### Positive

- Top-level consumers get a cleaner, more `HttpClient`-like API.
- Session mechanics move out of the common call path.
- Periphery keeps explicit lifecycle/status semantics where they matter.
- Protocol/application packages gain a clearer model for polished client APIs.
- The default operation path behaves predictably: immediate success when active,
  immediate failure when unavailable.

### Neutral

- `DeviceSessionHost<TSession>` remains necessary as the lower-level lifecycle primitive.
- Advanced consumers still have access to the full host/status model when they need it.

### Negative / watch points

- This introduces another public type to explain and maintain.
- The distinction between host and client must be documented clearly to avoid confusion.
- Callers that want wait-for-availability behavior must opt into it explicitly through
  host-level APIs or higher-level policy wrappers.

---

## References

- **REF-001**: ADR-0032 — `DeviceSessionHost<TSession>` as the lifecycle primitive that
  this facade composes.
- **REF-002**: ADR-0030 — Application-level reconnect behavior and lifecycle audit.
- **REF-003**: `docs/surface/periphery-session-integration-guide.md` — Layer model and
  lifecycle ownership principles.
- **REF-004**: `HttpClient` — inspirational reference for stable facade shape over
  lower-level connection churn.
