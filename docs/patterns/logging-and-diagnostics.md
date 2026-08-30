# Logging and diagnostics

> **Read this before** adding logging, metrics, or any other observability
> surface to a `Periphery.*` package — including new extension packages
> like `Periphery.Usb`, `Periphery.Monitor`, or `Periphery.Bootloader.*`.
> These conventions are repo-wide; we want a single, predictable shape
> across every published package.

This document defines how Periphery libraries emit logs, metrics, and
programmatic diagnostic events. It is the contributor counterpart to the
ADR-shaped decision; the substance is identical, this is the
day-to-day reference.

---

## TL;DR

| Concern                | Use                                         | Hot-path? |
|---|---|---|
| Logs                   | `ILogger<T>` with structured templates      | `[LoggerMessage]` source generation |
| Counts and distributions | `System.Diagnostics.Metrics` (`Meter`, `Counter<T>`, `UpDownCounter<T>`, `Histogram<T>`, `ObservableGauge<T>`) | Always low-overhead by design |
| Cross-call correlation | `ILogger.BeginScope` with structured properties | Open once per bounded operation |
| Programmatic events    | A focused, package-defined callback interface or `INotifyPropertyChanged` | Not applicable |
| Errors the caller can act on | A typed exception (`CameraDeviceLostException`, `CameraTimeoutException`, …) | Logged only if thrown across a boundary the caller can't observe |
| Distributed tracing    | Deferred. Not in v1.                        | — |

---

## 1. Primary logging abstraction — `ILogger<T>`

Every `Periphery.*` library logs through `ILogger<T>` from
`Microsoft.Extensions.Logging.Abstractions` (the abstractions-only package —
never `Microsoft.Extensions.Logging`). We do not depend on a logging
*provider* (Serilog, NLog, console). Consumers wire their own.

Four shipping libraries carry the `PackageReference` themselves: `Periphery`,
`Periphery.Camera`, `Periphery.Usb`, and `Periphery.Treehopper`. So do the two
unpackaged helpers, `Periphery.Diagnostics` and `Periphery.Treehopper.Flasher`.
Every other library takes no logging reference of its own. They log through the
static `PeripheryLoggerFactory` in the core and pick the package up
transitively.

For types constructed via DI, take `ILogger<T>` as a constructor
parameter. For types constructed directly via factory methods (most
Periphery surfaces — `CameraDevice.OpenAsync`, `CameraSession.OpenAsync`,
`DeviceTracker.For(...)`), accept `ILogger<T>?` and fall back to
`NullLogger<T>.Instance`:

```csharp
public sealed class CameraSession
{
    private readonly ILogger<CameraSession> _logger;

    internal CameraSession(
        // … existing params …
        ILogger<CameraSession>? logger = null)
    {
        _logger = logger ?? NullLogger<CameraSession>.Instance;
    }
}
```

The logger is plumbed through factory `OpenAsync` methods as a final
optional parameter (after the `CancellationToken`).

`ILogger<T>` always uses the implementing type as `T` — the category
name is then the fully-qualified type name and consumers can filter
precisely. Don't pass `ILogger` (untyped) and don't construct categories
from strings.

---

## 2. Structured templates — always

Every log call uses a semantic message template with structured
parameters. Strings are not concatenated into the message at the call
site.

```csharp
// Good — {DeviceName} etc. become stable structured properties consumers can filter on.
_logger.LogInformation(
    "Session opened on {DeviceName} at {Width}x{Height} {PixelFormat}",
    device.Name, format.Width, format.Height, format.PixelFormat);

// Avoid — loses the structured property names. (Modern .NET 6+ does
// short-circuit the message-string allocation when the level is
// disabled via LoggerMessageInterpolatedStringHandler, so the runtime
// cost case is weaker than it used to be — but the lost structure is
// the lasting cost, and for hot paths source-gen avoids both.)
_logger.LogInformation(
    $"Session opened on {device.Name} at {format.Width}x{format.Height}");
```

Template parameter names are PascalCase. They become structured property
names in any provider that respects the template (Serilog, OpenTelemetry,
the .NET console formatter, etc.).

---

## 3. Scopes for correlation

Any sequence of logs that belongs to a single bounded operation — a
capture session, a USB transfer batch, a reconnect attempt, a host
status transition window — gets a logger scope wrapped around the work.
Every log emitted inside the scope inherits its structured properties,
so consumers can filter or join on them without us threading a
correlation ID through every method:

```csharp
public async IAsyncEnumerable<LeasedCameraFrame> CaptureAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var scope = _logger.BeginScope(
        "Session={DeviceId} Endpoint={NativeEndpoint}",
        DeviceInfo.Id, NativeEndpointId);

    LogProducerStarted(_logger, _backend.GetType().Name, Options.BufferCount);
    try
    {
        await foreach (var frame in _channel.Reader.ReadAllAsync(ct))
        {
            // Every Trace log here inherits Session and Endpoint —
            // no need to re-thread them through LogFrameProduced.
            yield return frame;
        }
    }
    finally
    {
        LogProducerStopped(_logger, _produced, _dropped, _sw.Elapsed.TotalSeconds);
    }
}
```

Rules:

- One scope per bounded operation. Don't open a scope per log call.
- Scopes work transparently with `[LoggerMessage]` calls — the source-generated
  method picks up ambient scope state through the `ILogger` it receives.
- Use stable property names (`Session`, `Endpoint`, `Attempt`) — consumers
  will filter on them.
- Keep nesting shallow (≤2 levels). Deeper nesting is a sign you should
  be using `ActivitySource` and distributed tracing instead — which is
  deferred to a future ADR (§11).

---

## 4. Log level conventions

Apply these consistently across every package. The level is what
distinguishes a "noise during debugging" entry from a "wake the operator
at 3 a.m." entry; getting it right matters more than the exact wording.

| Level         | Use for                                                                                  | Periphery examples                                                              |
|---|---|---|
| **Trace**     | Per-item hot-path detail, normally off in production                                     | Per-frame timestamp+size, per-USB-transfer payload, per-MIDI-message dispatch   |
| **Debug**     | Lifecycle transitions, internal state changes, decision points                            | Backend opened, watcher started/restarted, host status `DeviceAbsent → SessionStarting`, exhaustion-policy fallback |
| **Information** | Session-level events visible in normal operation                                       | `CameraSession` opened/closed, `DeviceSessionHost` reached `SessionActive`, device added/removed edge events |
| **Warning**   | Degraded but recoverable                                                                  | Frame dropped under `BufferExhaustionPolicy` (first, then every hundredth), producer stalled, watcher reconnect attempt, retry, USB transfer retried after stall |
| **Error**     | An operation failed                                                                       | Backend faulted mid-capture, worker loop crashed, COM HRESULT mapped to a thrown exception (logged only at the throw site if the caller can't observe it) |
| **Critical**  | Process-compromising failure                                                              | Pool corruption, missing required interop dependency at startup                  |

Rules of thumb:

- If you'd want to see it on a production dashboard during normal
  operation, it's **Information**.
- If you'd only enable it while debugging a specific issue, it's
  **Debug** or **Trace**.
- If something went wrong but the system recovered, it's **Warning**.
- If the current operation failed, it's **Error**.

### When to log Error vs throw

Periphery has a deliberate exception hierarchy
(`CameraException`, `CameraDeviceLostException`, `CameraTimeoutException`,
`CameraConfigurationException`, etc.). Prefer **throwing** when the
caller can act on the failure. Log at **Error** level only when:

- the failure happens in a worker loop the caller doesn't observe
  directly (e.g., the camera producer task on a background thread), or
- the failure happens in a callback the caller registered and we caught
  it to keep the host alive (e.g., `whileSessionActive` on
  `DeviceSessionHost<T>`), or
- you're about to throw and want a structured record at the point of
  origin before the exception propagates.

Don't log at Error and then throw the same context — duplicate noise.
Log *or* throw, not both. The exception is: cleanup/disposal paths
where the exception cannot be propagated (e.g., a callback that fired
during dispose) — there, log-and-swallow is the right pattern, because
the alternative is a process-killing unhandled exception.

---

## 5. Hot paths use `[LoggerMessage]` source generation

In any tight loop or per-item path, log calls go through the source
generator. This eliminates message-string allocation, parameter boxing,
and template parsing when the level is disabled — the generated method
checks `IsEnabled` first and returns immediately.

Concrete Periphery hot paths that **must** use `[LoggerMessage]`:

- `CameraSession` producer loop (per-frame)
- `CameraFramePool.TryDeliver`/`Return` (per-frame, but limit to Trace
  there — these run hundreds of times per second)
- USB transfer completion handlers (future `Periphery.Usb`)
- HID input report dispatch
- Serial framing inner loop
- MIDI message parse/dispatch

Example shape:

```csharp
public sealed partial class CameraSession
{
    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Frame {FrameIndex}: {ByteCount} bytes, ts={TimestampMs:F1}ms")]
    private static partial void LogFrameProduced(
        ILogger logger, long frameIndex, int byteCount, double timestampMs);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Frame dropped (#{DroppedCount}); pipeline full under {Policy}. Outstanding={Outstanding}")]
    private static partial void LogFrameDropped(
        ILogger logger, long droppedCount, BufferExhaustionPolicy policy, int outstanding);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Backend producer faulted on {NativeEndpoint}")]
    private static partial void LogProducerFaulted(
        ILogger logger, string nativeEndpoint, Exception ex);
}
```

Requirements:

- Containing class is `partial`.
- Log methods are `private static partial void`.
- First parameter is always `ILogger logger` (untyped — the call site
  passes `_logger`).
- Exception parameters go last and are *not* interpolated into the
  message — the framework attaches them as the structured `Exception`
  property of the entry. Don't write `{Ex}` in the template.
- Use format specifiers in templates for numeric precision
  (`{ElapsedMs:F2}`, `{Throughput:N0}`).

### Why source-gen specifically

The standard `ILogger` extension methods (`LogTrace`, `LogDebug`, …)
evaluate their arguments **before** the `IsEnabled` check inside the
call. So:

```csharp
_logger.LogTrace("Frame state: {Json}", frame.SerializeVerbose());
```

…runs `SerializeVerbose()` even when Trace is disabled. Modern .NET
short-circuits the message-string allocation through the
interpolated-string handler, but it cannot short-circuit method-call
arguments — only source-gen does. This is the load-bearing reason for
`[LoggerMessage]` in tight inner loops, beyond the allocation savings.

For non-hot-path code (open/close, configuration, error reporting,
host status transitions), the standard `ILogger` extension methods are
acceptable and idiomatic:

```csharp
_logger.LogInformation(
    "Session closed: produced={FramesProduced} dropped={FramesDropped} duration={DurationSec:F2}s",
    metrics.FramesProduced, metrics.FramesDropped, sw.Elapsed.TotalSeconds);
```

---

## 6. Naming conventions for source-generated log methods

| Pattern                  | Use                                                  | Example                       |
|---|---|---|
| `Log{Event}`             | General lifecycle events                              | `LogStarted`, `LogStopped`, `LogDisposed` |
| `Log{Subject}{Event}`    | Subsystem-scoped events                               | `LogBackendFaulted`, `LogSessionEnded`, `LogWatcherRestarted` |
| `Log{Severity}{Subject}` | When severity is the distinguishing factor            | `LogWorkerFaulted`, `LogDisposeTimeout` |
| `LogPeriodic{Subject}`   | Throttled periodic snapshots                          | `LogPeriodicStatus`, `LogPeriodicMetrics` |

Group all log methods at the bottom of the class, separated by a
banner comment that matches the rest of Periphery's section style:

```csharp
public sealed partial class CameraSession
{
    // … instance members, public API, internal worker loop …

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Producer started on backend {BackendType} with {BufferCount} buffers")]
    private static partial void LogProducerStarted(
        ILogger logger, string backendType, int bufferCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Producer stopped: produced={FramesProduced} dropped={FramesDropped} duration={DurationSec:F2}s")]
    private static partial void LogProducerStopped(
        ILogger logger, long framesProduced, long framesDropped, double durationSec);
}
```

---

## 7. Quantitative telemetry — `System.Diagnostics.Metrics`

Logs are for *events*. For *quantities* use
`System.Diagnostics.Metrics`. It is the .NET-built-in path to
OpenTelemetry-compatible counters and histograms with no allocation
overhead and no third-party dependency.

### One Meter per package

Each published package gets exactly one `Meter`, named after the
package and versioned with the assembly version:

```csharp
// in Periphery.Camera
internal static class CameraMeters
{
    internal static readonly Meter Meter = new(
        name: "Periphery.Camera",
        version: typeof(CameraMeters).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
```

Names already established / planned across the family:

| Package                         | Meter name                          |
|---|---|
| `Periphery`                     | `"Periphery"`                       |
| `Periphery.Camera`              | `"Periphery.Camera"`                |
| `Periphery.Hid`                 | `"Periphery.Hid"`                   |
| `Periphery.Monitor`             | `"Periphery.Monitor"`               |
| `Periphery.Usb`                 | `"Periphery.Usb"`                   |
| `Periphery.Treehopper`          | `"Periphery.Treehopper"`            |
| `Periphery.Bootloader`          | `"Periphery.Bootloader"`            |

The rule is one meter per package, named for the package. `Periphery.Camera.Pipelines`
appeared in earlier revisions of this table; it was never built
([ADR-0045](../adr/0045-substrate-independence-from-crossbar.md)).

A consumer adds OpenTelemetry, Prometheus, or App Insights and filters
by `meter.name` — they get a clean per-package view.

### Instrument naming

Lowercase dot-separated names following OpenTelemetry semantic
convention style: `periphery.<subsystem>.<measure>`. Suffix unit-bearing
metrics with the unit (`_ms`, `_bytes`, `_total`).

```csharp
internal static readonly Counter<long> FramesProducedCounter =
    CameraMeters.Meter.CreateCounter<long>(
        "periphery.camera.frames_produced",
        unit: "{frame}",
        description: "Total camera frames delivered to consumers.");

internal static readonly UpDownCounter<int> OutstandingLeasesGauge =
    CameraMeters.Meter.CreateUpDownCounter<int>(
        "periphery.camera.outstanding_leases",
        unit: "{lease}",
        description: "Number of leased camera frames currently held by consumers.");

internal static readonly Histogram<double> FrameLatencyHistogram =
    CameraMeters.Meter.CreateHistogram<double>(
        "periphery.camera.frame_latency_ms",
        unit: "ms",
        description: "Latency from backend frame arrival to consumer lease.");
```

### Choosing the instrument type

| Signal                       | Use                                                                            |
|---|---|
| **`Counter<long>`**          | Monotonically increasing totals: frames produced, edge events, retries, errors |
| **`UpDownCounter<int>`**     | Values that go up and down with paired increments/decrements: outstanding leases, in-flight transfers |
| **`Histogram<double>`**      | Distribution of values: frame latency, transfer size, batch size               |
| **`ObservableGauge<int>`**   | Sampled state read at scrape time: free pool buffers, current backend status, queue depth |
| **ILogger Trace/Debug**      | Per-item *context* with human-readable detail, gated for active debugging      |
| **ILogger Information+**     | Discrete *events* with structured context: "session opened", "watcher restarted" |

Use logs and metrics together when appropriate. The classic pairing: a
counter increments on every dropped frame *and* a Warning log fires the
first time it happens (or every Nth time after) with the surrounding
context. The counter answers "how many?"; the log answers "what was
happening when?".

### Single source of truth when a metric is also exposed publicly

Some Periphery types — `CameraSessionMetrics`, planned
`DeviceTrackerMetrics` — mirror canonical metrics on a structured
snapshot for in-process supervision (per ADR-0035 §6b). The mental
model is **one internal counter, multiple readers**, not parallel
surfaces to keep in sync:

```csharp
// One internal counter.
private long _framesProduced;

// One increment site: bumps the field AND records to the Meter.
private void OnFrameProduced()
{
    Interlocked.Increment(ref _framesProduced);
    FramesProducedCounter.Add(1);
}

// Snapshot reads the same field.
public CameraSessionMetrics Metrics => new(
    FramesProduced: Interlocked.Read(ref _framesProduced),
    /* … */);
```

The trap to avoid: keeping a private `long` for the snapshot **and** a
separate `Counter<long>` for the meter, and incrementing them at
different call sites. They will drift, silently, until someone
investigates an inconsistency between the dashboard and in-process
supervision.

---

## 8. Periodic status logging

For long-running processing loops, emit a Debug-level periodic status
log that summarizes cumulative progress.

For loops with **bounded item rate** (30 fps frame producer, 60 Hz HID
input poll, throttled work), gate on a modulo check:

```csharp
if (frameIndex % 500 == 0)
{
    LogPeriodicStatus(
        _logger,
        frameIndex,
        sw.Elapsed.TotalSeconds,
        metrics.FramesDropped,
        metrics.OutstandingLeases);
}
```

For loops with **unbounded or highly variable rate** (USB bulk
transfers, network IO bursts), gate on **wall-clock time** instead —
modulo gating produces wildly different log frequencies depending on
throughput:

```csharp
if (sw.Elapsed - _lastStatusLog >= TimeSpan.FromSeconds(5))
{
    LogPeriodicStatus(_logger, /* … */);
    _lastStatusLog = sw.Elapsed;
}
```

The cadence rule of thumb:

- **Above ~100 items/sec, default to time-gating** (every 5–30s).
  A 60 MB/s transfer with 1 KB packets logging every 500 items prints
  every ~8 ms; that's noise.
- **10–1,000 items/sec with bounded rate**: count-gating every 50–500
  items is fine.
- **<10 items/sec**: time-gating every 30–60s, or count-gating every
  10 items — whichever produces the simpler call site.

In a production build with Debug filtered out, the call is a no-op.

---

## 9. Lifecycle event logging

Periphery has a *lot* of lifecycle: device tracker watchers,
`DeviceProxy<T>` opens, `CameraSession` runtimes,
`DeviceSessionHost<T>` status transitions. Apply these consistently:

| Event                                  | Level         | What to include                                                |
|---|---|---|
| Backend / component started            | Debug         | Configuration summary (buffer counts, options chosen)           |
| Backend / component stopped            | Information   | Cumulative stats (frames produced, errors, duration)            |
| Component disposed                     | Debug         | Lifetime stats if different from stop                           |
| State transition (host status, etc.)   | Debug         | From-state, to-state, reason                                    |
| Resource opened (device, session)      | Information   | Identity (device name, native endpoint id), key parameters     |
| Resource closed                        | Information   | Identity + cumulative stats                                     |
| Worker faulted                         | Error         | Worker name + exception                                         |
| Watcher restarted after fault          | Warning       | Restart attempt number, last error                              |
| Reconnect attempt (host)               | Warning       | Attempt number, last error                                      |

Stop logs are deliberately **Information**, not Debug — they carry the
session summary that operators need without enabling Debug. Open logs
are Information for the same reason: they correlate with downstream
events the operator cares about.

State-transition logs (e.g., `DeviceAbsent → SessionStarting →
SessionActive`) are Debug because the meaningful arrival points
(`SessionActive` reached, session ended) already get Information-level
logs. Don't double-log.

---

## 10. Programmatic diagnostic seams

When a consumer needs to *react* to events in code rather than just
observe logs, expose a focused, purpose-built surface — never ask
consumers to parse log streams. Periphery already has several:

- **`CameraSession.Metrics`** — structured snapshot for supervision
  policy.
- **`DeviceSessionHost<T>` `INotifyPropertyChanged`** — host status
  changes for UI binding and reconnect orchestration.
- **`DeviceTracker` edge events** (`Connected` / `Disconnected`) —
  programmatic device-presence observation.
- **The exception hierarchy** — typed, catchable, contains the
  diagnostic context (e.g., `CameraDeviceLostException.DeviceId`).

When adding a new diagnostic surface to a package, ask first whether
one of the existing patterns covers it. New ones should be:

- a single small interface or `INotifyPropertyChanged` property,
- focused on the package's specific concept,
- independent of log message text (consumers must not rely on log
  strings being stable),
- documented in the package's README and the relevant ADR.

Don't introduce a global "Periphery diagnostics bus" or a singleton
event aggregator. Each package exposes its own surface; consumers
compose them.

---

## 11. What this doc deliberately does *not* cover

- **Distributed tracing** (`ActivitySource`, OpenTelemetry traces).
  Deferred. Periphery is a device-I/O library; distributed traces are
  designed for service-to-service request correlation. If consumer
  demand appears, it can be added without disrupting the `ILogger`
  and `Metrics` foundations.
- **Logging providers**. We only ship `ILogger` usage. Consumers pick
  Serilog / NLog / OpenTelemetry / nothing.
- **EventSource / ETW**. Rejected for the same reasons we use
  `ILogger`: cross-platform, ecosystem support, structured by default.
- **Custom logging abstractions**. Hard no. `ILogger` is the .NET
  standard; introducing a Periphery-specific wrapper forces consumers
  to write adapters for work the ecosystem already solved.

---

## Quick reference

### Decision checklist for new code

1. **Hot path?** (per-frame, per-transfer, per-message)
   → `[LoggerMessage]` source-generated method.
2. **Lifecycle event?** (open / close / status change)
   → Standard `ILogger` extension method, level per the table in §4.
3. **A countable thing?** (frames produced, edge events, errors)
   → `Counter<long>` on the package's `Meter`.
4. **A value that goes up and down?** (outstanding leases, in-flight transfers)
   → `UpDownCounter<int>` on the package's `Meter`.
5. **A measurable distribution?** (frame latency, queue depth,
   transfer size)
   → `Histogram<double>` on the package's `Meter`.
6. **Sampled state read on scrape?** (free pool buffers, current status)
   → `ObservableGauge<int>` on the package's `Meter`.
7. **Bounded operation that emits multiple logs?** (session, transfer, reconnect)
   → Wrap in a `BeginScope` with stable property names.
8. **Would an operator want to see this in normal production?**
   → `Information`.
9. **Would a developer only care during active debugging?**
   → `Debug` or `Trace`.
10. **Will the caller see it through the API surface?**
    → Throw a typed exception. Don't also log.

### Skeleton for a new package component

```csharp
public sealed partial class MyComponent
{
    private static readonly Counter<long> ItemsCounter =
        MyPackageMeters.Meter.CreateCounter<long>(
            "periphery.mypackage.items_total",
            description: "Items processed.");
    private static readonly Histogram<double> LatencyHistogram =
        MyPackageMeters.Meter.CreateHistogram<double>(
            "periphery.mypackage.latency_ms",
            unit: "ms",
            description: "Per-item processing latency.");
    // Add UpDownCounter<int> for paired up/down values, ObservableGauge<int>
    // for sampled state, as needed.

    private readonly ILogger<MyComponent> _logger;

    internal MyComponent(/* … */, ILogger<MyComponent>? logger = null)
    {
        _logger = logger ?? NullLogger<MyComponent>.Instance;
    }

    public void Process(Item item)
    {
        using var scope = _logger.BeginScope("Item={ItemId}", item.Id);

        var sw = Stopwatch.StartNew();
        // … work …
        sw.Stop();

        ItemsCounter.Add(1);
        LatencyHistogram.Record(sw.Elapsed.TotalMilliseconds);
        LogItemProcessed(_logger, item.Id, sw.Elapsed.TotalMilliseconds);
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "Processed item {ItemId} in {ElapsedMs:F2}ms")]
    private static partial void LogItemProcessed(
        ILogger logger, string itemId, double elapsedMs);
}
```

---

## Cross-references

- [ADR-0032 — DeviceSessionHost](../adr/0032-device-session-host.md):
  status transitions and supervision boundaries informed by lifecycle
  logging.
- [ADR-0035 — Periphery.Camera §6b](../adr/0035-periphery-camera.md):
  separation of session readiness vs ongoing supervision is the reason
  metrics live on the snapshot *and* on `Meter`.
- [`source-generated-com-interop.md`](source-generated-com-interop.md):
  COM interop hazards. Errors logged at the COM/HRESULT boundary
  follow §4 of this document.
- [`usb-lifecycle-testing.md`](usb-lifecycle-testing.md) and
  [`wire-level-testing.md`](wire-level-testing.md): test strategies
  that observe the same logs and metrics this document defines.
