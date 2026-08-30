---
title: "ADR-0032: DeviceSessionHost — First-Class Session Publication for Application-Level Hosts"
status: "Accepted"
status_note: "Shipped - `DeviceSessionHost<TSession>` and `MultiDeviceSessionHost`. Frontmatter previously read `Proposed` while the body read `Accepted`."
date: "2026-03-25"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "lifecycle", "session", "device-handle", "hosted-service", "composition"]
supersedes: ""
superseded_by: ""
---

# ADR-0032: DeviceSessionHost — First-Class Session Publication for Application-Level Hosts

## Status

> **Amendment (2026-06-11).** `DeviceSessionHost<TSession>` and
> `MultiDeviceSessionHost<TSession>` now forward an optional
> `IReconnectPolicy? reconnectPolicy` (added to every factory) down to the inner
> `DeviceProxy<SessionLease<TSession>>` they wrap — the reconnect policy is a
> device-level concern and the host is plumbing, so this is a forward, not a new
> concept (see ADR-0055 amendment #2). The host also surfaces the inner proxy's
> openability state outward: a `ConnectionState` property and a new terminal
> `HostStatus` discriminant `SessionGaveUp<TSession>` (distinct from the transient
> `SessionUnavailable`) so the session cohort feeds the same health evaluator as
> the `DeviceProxy`-direct cohort.

---

## Context

### 1. Periphery owns lifecycle; applications need to publish sessions

Periphery's `DeviceProxyBase`, `DeviceProxy<TDevice>`, and non-generic `DeviceProxy`
provide a strong, reconnect-resilient lifecycle owner. They correctly define the
active-session window through `OnConnectedAsync` / `onActivated` (init gate) and
`OnLoopAsync` / `onLoop` (active-session execution window).

Every application that builds a protocol-backed service on top of Periphery must answer
the same question: *how does the rest of the application access the currently active
session while the handle manages connect/disconnect/reconnect underneath?*

### 2. The repeated boilerplate pattern

The documentation in `docs/surface/examples_generic-session-host-example.md` and
`docs/surface/examples_modbus-over-periphery-serial-example.md` both show this
application-level layer, and both exhibit the same structure:

```csharp
// CTX-001: Repeated pattern across all composed session hosts

public sealed class SomeDeviceHost : IAsyncDisposable
{
    private readonly DeviceProxy _handle;         // (1) holds lifecycle owner
    private SomeSession? _current;                 // (2) nullable current session

    private SomeDeviceHost(DeviceProxy handle) { _handle = handle; }

    public static async Task<SomeDeviceHost> OpenAsync(...)
    {
        SomeDeviceHost? host = null;               // (3) two-phase init variable
        SomeResource? resource = null;             // (4) extra closure state

        var handle = await DeviceProxy.OpenAsync(
            profile: profile,

            onActivated: async (deviceInfo, ct) =>
            {
                if (host is null)                  // (5) null guard antipattern
                    throw new InvalidOperationException();

                host._resource = OpenResource(deviceInfo);
                resource = CreateAdapter(host._resource);
            },

            onDeactivated: _ =>
            {
                if (host is not null)
                    host._current = null;          // (6) session withdrawal

                CleanUpResource(host?._resource);
                return Task.CompletedTask;
            },

            onLoop: async ct =>
            {
                if (host is null || resource is null)
                    throw new InvalidOperationException();

                var client = CreateClient(resource);
                host._current = new SomeSession(client);  // (7) session publication

                try
                {
                    await Task.Delay(Timeout.Infinite, ct);// (8) loop as availability gate
                }
                finally
                {
                    host._current = null;          // (9) session withdrawal on exit
                }
            },

            ct: cancellationToken);

        host = new SomeDeviceHost(handle);         // (10) post-init assignment
        return host;
    }

    public SomeSession GetRequiredSession()        // (11) repeated access helper
        => _current ?? throw new InvalidOperationException("No active session.");

    public ValueTask DisposeAsync()                // (12) trivial delegation
        => _handle.DisposeAsync();
}
```

The same twelve structural elements appear in every variation. This is not accidental —
it reflects a real and universal need. But it is entirely mechanical, and its current
shape has a significant latent defect.

### 3. The two-phase initialization defect

- **CTX-002**: The pattern requires `host` to be assigned *after* the handle is created
  but *before* any of the delegates execute. The variable is initialized as `null` and
  then assigned after `DeviceProxy.OpenAsync` returns.

- **CTX-003**: `DeviceProxy.OpenAsync` calls `watcher.StartAsync(ct)` internally, which
  seeds the initial device snapshot. If a matching device is already present, this fires
  `Activated` on the tracker, which fires `StateChanged`, which schedules
  `TryActivateAsync` and eventually `RunLoopAsync` as fire-and-forget tasks. These tasks
  begin executing concurrently during the `await` in `OpenAsync`.

- **CTX-004**: There is no language-level guarantee that `host` will be assigned before
  `onActivated` or `onLoop` execute. If the fire-and-forget lifecycle tasks proceed fast
  enough to enter `onActivated` or `onLoop` before control returns to the line
  `host = new SomeDeviceHost(handle)`, the null guard throws, aborting the first
  connection attempt and deferring to the reconnect path.

- **CTX-005**: The resulting behavior is non-deterministic: on a busy system the first
  activation may fail spuriously and reconnect after the backoff delay (1 s → 2 s → …).
  On most systems the null guard is never hit in practice because the task scheduler
  yields during the `await watcher.StartAsync(ct)` before executing the lifecycle tasks.
  But this is not a contract guarantee — it is scheduling luck.

- **CTX-006**: The null guards (`if (host is null) throw`) exist specifically to work
  around this structural problem. They are not defensive checks that express meaningful
  domain intent.

### 4. The loop-as-availability-gate misuse

- **CTX-007**: `onLoop` is designed as the active-session execution window for
  long-running per-connection work. In a shared-client scenario (where multiple callers
  invoke protocol operations), the correct use of `onLoop` is:
  publish the session → wait until cancelled → unpublish the session.

- **CTX-008**: But this means `onLoop` is regularly reduced to a wrapper around
  `await Task.Delay(Timeout.Infinite, ct)`. That is functionally correct, but it ties up
  the loop execution window for a concern that Periphery could own directly.

- **CTX-009**: When `onLoop` is `Task.Delay(Infinite)` the only thing making it a loop
  is the reconnect behavior on exit. Calling this a "loop" is misleading from the
  consumer's perspective; it is really an *availability gate*.

### 5. Duplication of session store plumbing

- **CTX-010**: Every host reimplements the same session storage: a nullable private
  field, assignment on activation, clearing on deactivation, and a `GetRequiredSession()`
  (or `TryGetCurrentSession()`) accessor. The patterns
  `docs/surface/examples_generic-session-host-example.md` and
  `docs/surface/examples_modbus-over-periphery-serial-example.md` both show separate
  `SessionStore` classes or equivalent inline logic.

- **CTX-011**: There is no variation in this logic across implementations. The session
  field is written exactly once per connection (on activation) and cleared exactly once
  per disconnection. The accessor either throws or returns null when disconnected.

### 6. Periphery should own what it is responsible for

- **CTX-012**: The patterns guide (`docs/surface/periphery-session-integration-guide.md`)
  states the layering principle: *"Periphery owns lifecycle. Communication layers own
  message exchange. Protocol and application layers own semantics and policy."*

- **CTX-013**: Session publication and withdrawal — knowing when a session is alive and
  making it accessible to callers — is part of lifecycle management, not protocol
  semantics. The handle already manages `IsConnected` and `Device`; managing a
  derived `TSession` derived from an active connection is a natural extension of that
  same responsibility.

- **CTX-014**: The session integration guide explicitly recommends that every
  Periphery-backed host follow the same seven-step composition model
  (open resource → adapt bytes → wrap communication → create client → publish session →
  call through boundary → disconnect withdraws session). Giving that composition a
  first-class API in Periphery eliminates the need to re-derive it in every application.

---

## Decision

Introduce `DeviceSessionHost<TSession>` as a new first-class type in Periphery.

`DeviceSessionHost<TSession>` extends `DeviceProxyBase<TDevice, TException>` through
a specialized internal shape that manages session creation, publication, and withdrawal
automatically. The full composition — resource opening, session construction, session
lifetime, and session access — is encapsulated in one type.

---

### DEC-001: New discriminated union `HostStatus<TSession>`

`bool IsConnected` alone does not tell a consumer *why* there is no session, which
determines the correct response: fail fast, wait briefly, or stop trying.

Four states cover the full lifecycle of a `DeviceSessionHost<TSession>`:

```csharp
/// <summary>
/// Discriminated union describing the current observable status of a
/// <see cref="DeviceSessionHost{TSession}"/>.
/// </summary>
public abstract record HostStatus<TSession> where TSession : class;

/// <summary>
/// No device matching the profile is present and active in the device tree.
/// No session can be created until the device appears.
/// </summary>
/// <remarks>
/// The device may never have been seen, or it may have left the device tree
/// after a prior connection. Either way, reconnect cannot proceed until the
/// OS reports the device as active again.
/// </remarks>
public sealed record DeviceAbsent<TSession>() : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// A matching device is present and active; <c>createSession</c> is currently
/// running. A session will be published shortly if creation succeeds.
/// </summary>
public sealed record SessionStarting<TSession>(DeviceInfo Device) : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// A session is active and ready for use.
/// </summary>
public sealed record SessionActive<TSession>(TSession Session, DeviceInfo Device)
    : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// The device is present and active in the device tree, but no session is
/// currently available. The host is waiting with backoff before retrying.
/// </summary>
/// <param name="Device">The active device, still present in the device tree.</param>
/// <param name="LastError">
/// The exception from the most recent <c>createSession</c> failure, or
/// <see langword="null"/> if the session closed cleanly (e.g., device physically
/// disconnected and immediately reconnected).
/// </param>
/// <param name="Attempt">
/// The number of reconnect attempts so far during this unavailability window.
/// Resets to zero each time a session becomes active.
/// </param>
public sealed record SessionUnavailable<TSession>(
    DeviceInfo Device,
    Exception? LastError,
    int Attempt) : HostStatus<TSession>
    where TSession : class;
```

**State transition diagram:**

```
                  ┌────────────────────────────────────────────────┐
                  │                                                │
      Device appears                                   Device leaves tree
                  ▼                                                │
           DeviceAbsent ──────────────────────────────────────────┘
                  │
      Tracker becomes active
                  │
                  ▼
         SessionStarting ──── createSession throws ──► SessionUnavailable
                  │                                          │
      createSession succeeds                     backoff expires; retry
                  │                                          │
                  ▼                                          ▼
           SessionActive ─────── session ends ──────► SessionUnavailable
                  │               (disconnect,                │
      (happy path)│               fault, or                  │
                  │               loop exit)         Device leaves tree
                  │                                          │
                  └──────────────────────────────────────────▼
                                                       DeviceAbsent
```

The state machine answers the three consumer questions precisely:

| Status | "Is it worth waiting?" | Recommended action |
|---|---|---|
| `DeviceAbsent` | Unknown — device isn't present | Fail fast; it may never appear |
| `SessionStarting` | Yes — resolves in seconds | Wait or check again shortly |
| `SessionActive` | N/A | Use `Session` directly |
| `SessionUnavailable` | Yes — reconnect is automatic | Wait; check `LastError` and `Attempt` for diagnostics |

### DEC-002: New type `DeviceSessionHost<TSession>`

```csharp
/// <summary>
/// A reconnect-resilient device host that creates, publishes, and withdraws a
/// session-scoped object each time the tracked device connects. The host owns the
/// full lifecycle (connect / disconnect / reconnect); callers access the current
/// session through <see cref="GetRequiredSession"/> or <see cref="Status"/>.
/// </summary>
/// <typeparam name="TSession">
/// The session object type. May be any class; it does not need to implement
/// <see cref="IAsyncDisposable"/>. Use <paramref name="onSessionEnded"/> to
/// perform cleanup when the session is withdrawn.
/// </typeparam>
public sealed class DeviceSessionHost<TSession> : IAsyncDisposable
    where TSession : class
{
    // ── Factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a self-contained session host that owns its own watcher.
    /// </summary>
    public static Task<DeviceSessionHost<TSession>> OpenAsync(
        DeviceProfile profile,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a session host that borrows an existing tracker attached to a
    /// caller-owned watcher. Use when a single watcher powers multiple devices.
    /// </summary>
    public static DeviceSessionHost<TSession> Create(
        DeviceTracker tracker,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null);

    // ── State ────────────────────────────────────────────────────────────

    /// <summary>
    /// The current observable status of this host. Changes atomically as the
    /// lifecycle progresses. Subscribe to <see cref="StatusChanged"/> for
    /// notifications.
    /// </summary>
    public HostStatus<TSession> Status { get; }

    /// <summary>
    /// Convenience shorthand for <c>Status is SessionActive&lt;TSession&gt;</c>.
    /// </summary>
    public bool IsConnected { get; }

    /// <summary>The most recent device snapshot, or <see langword="null"/>.</summary>
    public DeviceInfo? DeviceInfo { get; }

    // ── Accessors ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current session, or throws <see cref="InvalidOperationException"/>
    /// if no session is active. The exception message includes the current
    /// <see cref="Status"/> so callers can diagnose why no session is available.
    /// </summary>
    public TSession GetRequiredSession();

    /// <summary>
    /// Returns <see langword="true"/> and sets <paramref name="session"/> when a
    /// session is active; otherwise returns <see langword="false"/>.
    /// </summary>
    public bool TryGetCurrentSession(
        [NotNullWhen(true)] out TSession? session);

    /// <summary>
    /// Waits asynchronously until a session becomes active and returns it.
    /// Throws <see cref="OperationCanceledException"/> if
    /// <paramref name="ct"/> is cancelled before a session is available.
    /// </summary>
    /// <remarks>
    /// If a session is already active when this is called, it returns
    /// immediately. If the status is <see cref="DeviceAbsent{TSession}"/>,
    /// the wait continues indefinitely until the device appears and a session
    /// is created — use <paramref name="ct"/> to bound the wait if necessary.
    /// </remarks>
    public Task<TSession> WaitForSessionAsync(CancellationToken ct = default);

    // ── Events ───────────────────────────────────────────────────────────

    /// <summary>
    /// Raised whenever <see cref="Status"/> changes. The event argument is the
    /// new status value.
    /// </summary>
    /// <remarks>
    /// Transitions fire in this order:
    /// <list type="number">
    /// <item><c>DeviceAbsent → SessionStarting</c> — device became active</item>
    /// <item><c>SessionStarting → SessionActive</c> — <c>createSession</c> succeeded</item>
    /// <item><c>SessionStarting → SessionUnavailable</c> — <c>createSession</c> threw</item>
    /// <item><c>SessionActive → SessionUnavailable</c> — session ended (disconnect or fault)</item>
    /// <item><c>SessionUnavailable → SessionStarting</c> — backoff elapsed, retrying</item>
    /// <item><c>SessionUnavailable → DeviceAbsent</c> — device left the device tree</item>
    /// </list>
    /// </remarks>
    public event EventHandler<HostStatus<TSession>>? StatusChanged;

    /// <inheritdoc/>
    public ValueTask DisposeAsync();
}
```

### DEC-003: `createSession` is the sole coupling point

The `createSession` delegate receives only `DeviceInfo` and a per-connection
`CancellationToken`. It is responsible for:

- opening any underlying resource (port, HID handle, stream, etc.),
- building any communication adapter (e.g., `IByteSource` over the resource),
- constructing any protocol/application client over that adapter,
- returning the fully ready `TSession` object.

The session object returned by `createSession` owns whatever per-connection resources
it needs. Cleanup of those resources is the responsibility of `onSessionEnded`.

This design means `DeviceSessionHost<TSession>` has no knowledge of intermediate layers
(raw transport, framing, protocol clients). It only knows whether a session exists.

```csharp
// Example: createSession builds all intermediate layers, returns the session only
var host = await DeviceSessionHost<ActiveModbusSession>.OpenAsync(
    profile: modbusProfile,
    createSession: (deviceInfo, ct) =>
    {
        var portName = deviceInfo.PortName!.Value.Value;
        var port = new SerialPort(portName, 19200) { ... };
        port.Open();
        var transceiver = Transceiver.Wrap(new SerialPortByteSource(port));
        var client = new ModbusRtuClient(transceiver);
        return Task.FromResult(new ActiveModbusSession(client, port)); // session owns port
    },
    onSessionEnded: session =>
    {
        session.Port.Close();
        session.Port.Dispose();
        return Task.CompletedTask;
    });
```

The `if (host is null) throw` null guard disappears entirely. The `createSession`
delegate is a pure function of `DeviceInfo` — no host reference is needed inside it.

### DEC-004: Eliminate the two-phase initialization defect

`DeviceSessionHost<TSession>.OpenAsync` constructs everything internally:
the `DeviceTracker`, `DeviceWatcher`, and the internal lifecycle state machine are all
created and configured before the watcher is started. No external `host` variable is
assigned after the fact.

The `createSession` delegate receives only `DeviceInfo`, which is available at the time
it is called — not at construction time. There is nothing to capture from the
not-yet-constructed host.

### DEC-005: Session lifetime is managed inside the state machine

The internal implementation uses the same `_openLock` and per-connection
`CancellationTokenSource` model as `DeviceProxyBase`. Session creation replaces
`OpenDeviceAsync` + `OnConnectedAsync`, and session withdrawal replaces
`OnDisconnectingAsync` + device disposal.

The `onLoop` slot is replaced by the session availability gate internally — callers
no longer need to write `await Task.Delay(Timeout.Infinite, ct)`. That pattern becomes
the implementation detail of `DeviceSessionHost`, not a consumer responsibility.

Concretely: the internal loop task waits for the per-connection `CancellationToken`.
Session creation fires an event and publishes `CurrentSession`. Cancellation withdraws
`CurrentSession`, fires `SessionEnded`, and calls `onSessionEnded`.

### DEC-006: Reconnect behavior is inherited unchanged from `DeviceProxyBase`

`DeviceSessionHost<TSession>` either extends `DeviceProxyBase` through an adapter shim
(see implementation notes) or replicates the same state machine model. Either way, the
reconnect contract from ADR-0027 and ADR-0030 applies without modification:

- `createSession` failure → `Status` transitions to `SessionUnavailable` (with `LastError`)
  → reconnect with backoff if tracker remains active.
- Session throws during its lifetime → session withdrawn → reconnect with backoff.
- OS-driven deactivation → session withdrawn → `Status` transitions to `DeviceAbsent`.
- `DisposeAsync` → session withdrawn (if active) → watcher stopped (if owned).

### DEC-007: Backward compatibility — `DeviceProxy` and `DeviceProxy<TDevice>` unchanged

`DeviceSessionHost<TSession>` is additive. No existing types change. The existing handle
types remain the correct choice for:

- dedicated loop workers (e.g., telemetry ingesters, scanner read loops),
- single-consumer scenarios where no session publication is needed,
- extension packages that override `OnLoopAsync` with device-specific behavior.

`DeviceSessionHost<TSession>` targets specifically the shared-service / session-publication
scenario described in `docs/surface/periphery-session-integration-guide.md`.

### DEC-008: `IHostedService` integration

Because `DeviceSessionHost<TSession>` is `IAsyncDisposable` and has no `StartAsync` /
`StopAsync` split, it composes naturally with `IHostedService`:

```csharp
public sealed class ModbusHostedService : IHostedService, IAsyncDisposable
{
    private DeviceSessionHost<ActiveModbusSession>? _host;
    private readonly DeviceProfile _profile;

    public ModbusHostedService(IOptions<ModbusOptions> options)
    {
        _profile = new DeviceProfile(f =>
        {
            f.OfCategory(DeviceCategory.Ports);
            f.WithUsbId(options.Value.VendorId, options.Value.ProductId);
        }, name: "Modbus Device");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _host = await DeviceSessionHost<ActiveModbusSession>.OpenAsync(
            _profile,
            createSession: (info, ct) => BuildSessionAsync(info, ct),
            onSessionEnded: s => s.DisposeAsync().AsTask(),
            ct: ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        return _host?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return _host?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    public ActiveModbusSession GetRequiredSession()
        => _host!.GetRequiredSession();

    // Optional: callers that need richer context can inspect Status directly
    // switch (_host!.Status)
    // {
    //     case SessionActive<ActiveModbusSession> { Session: var s, Device: var d }:
    //         // ready — use s
    //     case SessionUnavailable<ActiveModbusSession> { LastError: var err, Attempt: var n }:
    //         // reconnecting — err/n available for diagnostics
    //     case SessionStarting<ActiveModbusSession>:
    //         // connecting — use WaitForSessionAsync if blocking wait is acceptable
    //     case DeviceAbsent<ActiveModbusSession>:
    //         // device not plugged in
    // }
}
```

### DEC-009: Update pattern documentation

`docs/surface/examples_generic-session-host-example.md` and
`docs/surface/examples_modbus-over-periphery-serial-example.md` will be updated to
show `DeviceSessionHost<TSession>` as the recommended pattern. The old closure-based
shapes remain as reference examples of how the internal mechanism works, not as
recommended application code.

---

## Rationale

### 1. The duplication is mechanical, not incidental

The boilerplate in every session host is not expressing business logic. Twelve structural
elements (see Context §2) repeat verbatim across every implementation. That is the
definition of abstraction waiting to happen.

### 2. The two-phase init defect is real and subtle

The `host is null` null guard is not merely inelegant — it represents a genuine race
condition between the watcher's initial activation and the host variable assignment. The
defect manifests as a spurious connection failure on startup, which the reconnect path
silently recovers from. On many systems this is invisible; on others it delays first
availability by 1–5 seconds.

Making `DeviceSessionHost<TSession>` own the full construction eliminates the defect
structurally — not by catching it, but by making it impossible.

### 3. `onLoop` as `Task.Delay(Infinite)` is not a loop

The loop abstraction exists to describe *per-connection long-running work*. When a
session host uses `onLoop` purely to signal availability, the word "loop" is misleading
and the `Task.Delay(Infinite, ct)` idiom is boilerplate that Periphery should own.

Replacing this with an explicit session-publication lifecycle (`createSession` /
`Status` transitions / `onSessionEnded`) makes the consumer's intent explicit.

### 4. `bool IsConnected` alone is not enough diagnostic information

Consumers integrating `DeviceSessionHost` into larger services need to distinguish
*why* there is no session, not just that there isn't one. `bool IsConnected` only
answers "can I call `GetRequiredSession()` right now?" It does not answer:

- "Is the device even plugged in?" (`DeviceAbsent` vs. `SessionUnavailable`)
- "Is this a transient glitch or a persistent failure?" (`Attempt` on `SessionUnavailable`)
- "What went wrong?" (`LastError` on `SessionUnavailable`)
- "Is it safe to show a loading indicator vs. an error state?" (all four cases differ)

`HostStatus<TSession>` answers all four questions through pattern matching. `IsConnected`
remains as a convenience shorthand for the common case where only the yes/no is needed.

### 5. Session publication is a lifecycle concern

`IsConnected` and `Device` are already on the handle as first-class state. A
`TSession` derived from an active connection is structurally equivalent: it exists when
the device is active and is null otherwise. Extending the lifecycle model to publish
this is consistent with existing design intent.

### 5. Preserves all architectural layering

`DeviceSessionHost<TSession>` knows nothing about protocol, framing, or transport
specifics. It knows only that a `TSession` is created on connection and withdrawn on
disconnection. The `createSession` delegate is the boundary where application-specific
knowledge enters. This preserves the separation of concerns documented in
`docs/surface/periphery-session-integration-guide.md`.

---

## Alternatives Considered

### ALT-001: Document the existing pattern more clearly (no new type)

- **Description**: Add better documentation and examples showing the correct closure
  pattern, and note the null guard workaround.
- **Rejection reason**: Documentation does not eliminate the two-phase init defect or
  the boilerplate. Every future session host still needs to repeat all twelve structural
  elements. The pattern is already documented; the problem is that Periphery does not
  provide a first-class home for it.

### ALT-002: `DeviceProxy.OpenSessionAsync<TSession>(...)` as an extension method

- **Description**: Add a static extension or factory helper that returns a handle
  pre-wired to publish a session, without introducing a new named type.
- **Rejection reason**: A new named type (`DeviceSessionHost<TSession>`) is discoverable
  in IDE autocompletion, documentable, and expressible in DI registrations. An extension
  method returns `DeviceProxy` (or a tuple), which loses the typed `CurrentSession`
  property and the session-specific events. The named type also correctly separates
  "handle-oriented lifecycle" from "session-oriented lifecycle" at the API level.

### ALT-003: Require `TSession : IAsyncDisposable` and use `DeviceProxyBase<TSession, Exception>`

- **Description**: Since `DeviceProxyBase<TDevice, TException>` already manages the
  lifecycle of a `TDevice : IAsyncDisposable`, make `DeviceSessionHost<TSession>` simply
  be a `DeviceProxy<TSession>` where `TSession : IAsyncDisposable`.
- **Rejection reason**: Requiring `TSession : IAsyncDisposable` is an unnecessary
  constraint. Many useful session objects are plain records or classes with no disposable
  resources of their own. Introducing this constraint leaks implementation concerns
  (whether the session happens to hold disposable state) into the public type contract.
  The `onSessionEnded` callback achieves cleanup without constraining `TSession`.

### ALT-004: Two-factory construction (separate `Create` + `StartAsync`)

- **Description**: Separate object construction from watcher startup, allowing the host
  to be fully constructed before the watcher starts — eliminating the race condition.
- **Rejection reason**: This is the existing `DeviceProxy.Create(tracker, ...)` shape,
  which remains available for multi-device shared-watcher scenarios. For the
  self-contained case, `OpenAsync` is the established factory pattern in this codebase.
  Splitting construction and startup creates a different class of misuse (forgetting to
  call `StartAsync`).

---

## Implementation Notes

- **IMP-001**: `DeviceSessionHost<TSession>` can be implemented internally as a sealed
  class that holds a `DeviceProxy` instance (composition, not inheritance). The
  internal `DeviceProxy` is configured with `onActivated` = `createSession` adapter,
  `onDeactivated` = `onSessionEnded` adapter, and no `onLoop` (the availability gate is
  implemented internally by `DeviceSessionHost`). The `DeviceProxy`'s `onLoop` receives
  a delegate that simply awaits the per-connection `CancellationToken`.

- **IMP-002**: `Status` is a single atomically-replaced reference field. It is written
  only inside the `_openLock` (or its equivalent in the internal state machine) and is
  safe for concurrent reads outside the lock. A `volatile` backing field is appropriate.
  `IsConnected` reads `Status is SessionActive<TSession>`.

- **IMP-003**: `StatusChanged` must be raised after `Status` is updated and must be
  protected by the same `TryNotify` pattern used in `DeviceProxyBase` so that a
  throwing event handler cannot corrupt the internal lifecycle.

- **IMP-004**: `GetRequiredSession()` should throw with a message that includes both the
  device profile name and the current `Status` discriminant to make disconnected-state
  errors diagnosable in logs:
  `"No session available for 'Modbus Device' (current status: SessionUnavailable, attempt 3, last error: ...)"`.

- **IMP-005**: `WaitForSessionAsync` can be implemented with a
  `TaskCompletionSource<TSession>` that is completed when `Status` transitions to
  `SessionActive`. If `Status` is already `SessionActive` at call time it returns
  synchronously. It must register for `StatusChanged` and un-register on completion or
  cancellation.

- **IMP-006**: `DeviceSessionHost<TSession>.Create(tracker, ...)` (shared watcher factory)
  should call `CheckInitialState()` (the same mechanism used by `DeviceProxy.Create`)
  to handle already-active trackers correctly.

- **IMP-007**: `onSessionEnded` should be called in a `try/catch` (swallowing exceptions,
  matching the behavior of `OnDisconnectingAsync` in `DeviceProxyBase`) so that cleanup
  failures do not corrupt the lifecycle state machine.

- **IMP-008**: When `createSession` throws, `Status` transitions to
  `SessionUnavailable(Device, LastError: exception, Attempt: n)`. The reconnect
  backoff should apply in this case (consistent with ADR-0030 init-gate failure behavior).
  `Attempt` increments on each retry and resets to 0 when `Status` transitions to
  `SessionActive`.

- **IMP-009**: The pattern documentation files
  (`docs/surface/examples_generic-session-host-example.md` and
  `docs/surface/examples_modbus-over-periphery-serial-example.md`) should be updated
  in the same commit that introduces `DeviceSessionHost<TSession>` to show the idiomatic
  usage.

---

## Consequences

### Positive

- All twelve structural elements from the repeated boilerplate pattern are replaced by
  a single `DeviceSessionHost<TSession>.OpenAsync(...)` call.
- The two-phase initialization defect (spurious first-activation failure) is eliminated
  structurally.
- `onLoop = Task.Delay(Infinite)` disappears from application code.
- Session access is provided uniformly by Periphery: `GetRequiredSession()`,
  `TryGetCurrentSession()`, `WaitForSessionAsync()`, and `Status` for richer diagnostics.
- `HostStatus<TSession>` gives consumers a pattern-matchable discriminated union that
  distinguishes all four lifecycle states: absent, starting, active, and unavailable.
- The type is naturally expressible as an `IHostedService` with one-line `StartAsync`
  and `StopAsync`.
- DI registration is simplified: `services.AddSingleton<DeviceSessionHost<TSession>>()`
  alongside the factory call at startup.

### Neutral

- `DeviceProxy` and `DeviceProxy<TDevice>` remain unchanged and correct for
  loop-worker scenarios.
- Application code that already uses the hand-rolled session host pattern continues
  to work; adoption is voluntary.

### Negative / watch points

- `DeviceSessionHost<TSession>` is another public type to document and maintain.
  It must be tested against the same lifecycle scenarios as `DeviceProxyBase` (ADR-0030
  PLN-010 test cases apply here too).
- If the session construction is complex (multiple layers of adapters and clients),
  `createSession` can become large. Application code is responsible for factoring that
  into readable helpers — `DeviceSessionHost` does not need to know about the internal
  layers.
- `onSessionEnded` receives the session after `CurrentSession` has been cleared and
  `SessionEnded` has fired. Any caller that holds a reference to the old session and
  calls into it after `SessionEnded` must expect that it may no longer be valid.

---

## References

- **REF-001**: ADR-0027 — `DeviceProxyBase` lifecycle design (foundation for session
  host reconnect behavior).
- **REF-002**: ADR-0030 — Application-level reconnect and lifecycle audit (init-gate
  failure reconnect, teardown isolation, event handler isolation — all apply to
  `DeviceSessionHost`).
- **REF-003**: `docs/surface/periphery-session-integration-guide.md` — Layer model
  and integration principles that define where `DeviceSessionHost` fits.
- **REF-004**: `docs/surface/heartbeat-and-session-supervision-guide.md` — Defines
  readiness probes as belonging in the init gate and ongoing liveness as belonging
  above the session host, not inside it.
- **REF-005**: `docs/surface/examples_generic-session-host-example.md` — The
  boilerplate this ADR replaces.
- **REF-006**: `docs/surface/examples_modbus-over-periphery-serial-example.md` — The
  concrete instantiation of the boilerplate, showing the Modbus composition.
