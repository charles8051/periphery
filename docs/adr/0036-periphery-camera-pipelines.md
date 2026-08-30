---
title: "ADR-0036: Periphery.Camera.Pipelines — Fluent Frame Processing Layer"
status: "Superseded"
date: "2026-05-06"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "camera", "pipelines", "computer-vision", "stream-processing", "api-design"]
supersedes: ""
superseded_by: "0042"
---

# ADR-0036: Periphery.Camera.Pipelines — Fluent Frame Processing Layer

## Status

> The pipeline-runtime design in §"Design" (typed `FramePacket`,
> `FramePipeline`, `Transform`/`Enrich`/`Observe`/`Broadcast`
> operators, fluent extension methods) is **not what landed**.
> An external graph substrate replaced `FramePipeline<T>` with a
> node-and-port runtime (`Graph`, `SourceNode<T>`, `SinkNode<T>`, etc.).
> ADR-0042 records that as-built integration. The ICameraFrameSink
> shape, the frame-flow sink-parity decision (§10), and the
> ownership-preservation principles remain in force; the runtime
> details below are historical.

> **Update 2026-05-15 / 16.** The `CameraFrameMemoryDomain` /
> `SupportedMemoryDomains` design points in §"Design" are
> superseded by [ADR-0041] (substrate adoption) and the substrate's own
> decision to prefer explicit conversions over implicit capability
> negotiation. The substrate has no per-sink memory-domain
> advertisement; `CameraFrameMemoryDomain` was to be replaced by the
> substrate's own memory-domain type on camera frame types (for
> inspection) but not on sinks. See [ADR-0041] for the migration
> plan.
>
> [ADR-0041]: ./0041-periphery-camera-crossbar.md

## Context

ADR-0035 defines `Periphery.Camera` as the low-level camera I/O foundation: open a
camera from `DeviceInfo`, inspect formats and controls, configure capture, and deliver
frames with explicit ownership semantics.

That layer is necessary but not sufficient for the applications most users actually want
to build. Real camera applications are rarely just "read frames in a loop." They are
usually processing graphs:

- preview to a display
- run inference
- draw overlays
- record or stream encoded output
- branch one source to multiple sinks
- attach and consume metadata produced by intermediate stages
- apply bounded buffering and explicit backpressure policies

The desired user experience is often a fluent chain such as:

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .RunInference(detector)
    .DrawBoxes()
    .EncodeAs(H264)
    .ToFileStream(output, ct);
```

This shape is attractive for three reasons:

1. It is readable at the application layer.
2. It is naturally extensible via domain-specific operators.
3. It matches how users mentally model computer-vision workflows.

However, there are four architectural risks if this style is bolted directly onto the
camera core package.

### Problem 1 — The core camera package would become too opinionated

`Periphery.Camera` already has to solve camera identity, configuration, controls,
permissions, frame ownership, and reconnect behavior. If it also becomes the home for
inference, overlays, encoding, graph branching, metadata propagation, and sink adapters,
the package will stop being a clear I/O foundation and start becoming an all-in-one
media stack.

### Problem 2 — A fluent chain can hide real costs

Video pipelines have observable semantics that are not just implementation details:

- frame ownership and lifetime
- copies vs zero-copy paths
- queueing
- buffering
- dropped frames
- thread hops
- branch fan-out
- blocking vs latest-frame semantics

A pipeline API that is too magical will look elegant while hiding the exact behavior that
high-performance callers need to reason about.

### Problem 3 — Plain frames are not enough once operators add meaning

The moment the pipeline includes `RunInference()`, `TrackObjects()`, `EstimatePose()`,
`DrawBoxes()`, or `EncodeAs()`, a naked frame is no longer the whole unit of work.

Operators need a place to attach derived information:

- detections
- segmentation masks
- tracking IDs
- encoder hints
- timestamps and sequence numbers
- calibration or coordinate transforms
- arbitrary extension-package annotations

This implies that the pipeline should flow an envelope or packet, not just a frame.

### Problem 4 — Extension packages need a stable collaboration surface

The goal is not merely to build one fluent API for a single package. The goal is to make
operators from different packages compose cleanly:

- `Periphery.Camera.Inference`
- `Periphery.Camera.Drawing`
- `Periphery.Camera.Encoding`
- `Periphery.Camera.Streaming`
- application-local custom operators

That requires a stable packet shape, typed metadata model, and ownership contract that
third-party and in-repo extensions can build on without being forced into a monolithic
closed framework.

---

## Decision

Introduce a **separate higher-level package** named `Periphery.Camera.Pipelines` for
fluent frame processing, metadata propagation, branching, and sink composition.

This ADR defines the aspirational, top-level composition model. It does **not** change
the critical-path deliverables of ADR-0035. The camera core remains the lower-level,
explicit I/O foundation.

### Decision 1 — Pipelines are a layer on top of camera core, not part of it

`Periphery.Camera` remains responsible for:

- camera identity and open/close
- format negotiation and controls
- frame capture
- frame ownership semantics
- reconnect behavior

`Periphery.Camera.Pipelines` adds:

- fluent frame-stream composition
- typed metadata attachments
- branching and fan-out
- sinks and terminal operators
- operator extension points

This keeps the lower-level camera package honest and keeps the higher-level API optional.

### Decision 2 — The pipeline flows a packet envelope, not a naked frame

The fundamental unit of processing is a frame packet:

```csharp
public sealed class FramePacket : IDisposable
{
    public ICameraFrame Frame { get; }
    public FrameMetadata Metadata { get; }
}
```

The load-bearing concept is stable:

- `ICameraFrame` comes from `Periphery.Camera` core and remains a minimal read-only
    surface
- ownership-transition operations such as `Copy()` stay on concrete frame types such as
    `LeasedCameraFrame`, not on `ICameraFrame`
- the packet owns the current frame payload
- the packet carries typed metadata produced by operators
- packet disposal also disposes the currently owned/leased frame payload

This allows operators to enrich the packet without forcing all such data onto the frame
type or `DeviceInfo`.

### Decision 3 — Metadata is typed and open-ended

The pipeline metadata model must be open to extension packages and custom operators, but
it should avoid collapsing into a string-keyed `object` dictionary as the primary model.

Preferred shape:

```csharp
public sealed class FrameMetadata
{
    public void Set<T>(T value) where T : class;
    public bool TryGet<T>(out T? value) where T : class;
    public T GetRequired<T>() where T : class;
}
```

This gives extension packages a typed collaboration surface:

- `RunInference()` can attach `DetectionResults`
- `TrackObjects()` can attach `TrackingResults`
- `EncodeAs()` can read `EncoderHints`
- application code can attach custom metadata types

String-keyed metadata may still exist as an escape hatch, but typed attachments are the
first-class model.

### Decision 4 — Operator categories are explicit

The fluent API should distinguish the kinds of work an operator performs.

The core categories are:

- **Source adapters** — `AsPipeline()`, `FromChannel()`, `FromAsyncEnumerable()`
- **Transforms** — replace or mutate the current frame payload
- **Enrichers** — add metadata without replacing the frame
- **Observers** — inspect packets for side effects without becoming the terminal sink
- **Branches** — split one source into multiple downstream paths
- **Sinks / terminals** — write to display, file, stream, encoder, or custom consumer

This vocabulary matters because ownership, copying, and backpressure differ by category.

### Decision 5 — Ownership remains explicit through the pipeline

ADR-0035's ownership rules remain in force. The pipeline layer must not hide them.

The contract is:

- if an operator only needs the frame for the duration of the current stage, it may work
  on the leased payload directly
- if an operator needs to retain frame data asynchronously, it must promote the frame to
  an owned copy explicitly
- operators that replace frame payloads are responsible for disposing the previous payload
  they consume
- the pipeline must never silently relocate or rewrite the memory backing a live leased
  frame

In short: fluent composition is allowed; ownership magic is not.

### Decision 6 — Branching is first-class

Linear pipelines are not enough. Many real applications need to preview, infer, and
record from one source concurrently.

The pipeline layer therefore treats branching / fan-out as a first-class feature, not a
hack built from ad hoc callbacks.

Example target shape:

```csharp
await camera
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .Broadcast(
        branch => branch.ToDisplay(view),
        branch => branch.RunInference(detector).ToObserver(resultsSink),
        branch => branch.EncodeAs(H264).ToFileStream(output, ct))
    .RunAsync(ct);
```

### Decision 7 — Backpressure and buffering are explicit pipeline concerns

The pipeline layer must not pretend that all operators run at camera speed.

Different stages have different latency and throughput profiles. Therefore queueing and
overflow policy are part of the model, not an implementation detail.

At minimum the pipeline runtime must support bounded queues and explicit policies such as:

- block upstream
- keep latest
- drop oldest
- drop newest

The API surface may expose these as stage options or runtime configuration, but the
behavior must be visible and configurable.

### Decision 8 — Extension authors add operators via extension methods

The fluent layer must remain open. The preferred extension mechanism is ordinary C#
extension methods over the pipeline abstractions, not a central registry.

Example:

```csharp
public static class InferencePipelineExtensions
{
    public static FramePipeline RunInference(
        this FramePipeline pipeline,
        IObjectDetector detector);
}
```

This allows in-repo and third-party packages to add operators naturally without the core
pipeline package needing to know about them.

### Decision 9 — Goal APIs are optimistic but non-binding

The fluent examples in this ADR are **goal use cases**, not a commitment to one exact
class name or method set. The guiding requirement is that the final API should make these
scenarios clean and unsurprising.

### Decision 10 — Sink contract is deliberately shaped to mirror frame-flow's `IVideoSink`

**Amendment (2026-05-09).** The pipeline package's terminal sink interface
(`ICameraFrameSink`) is shaped to match
frame-flow's `IVideoSink` (`src/FrameFlow.Media/IVideoSink.cs`, in a
separate and private repository) deliberately, so a future `FrameFlow.Camera` bridge package — which lives
in **frame-flow's** repository, not Periphery's, per the plugin-to-framework
convention — stays close to a pass-through rather than a translation.

Concretely the sink contract aligns on:

- `ValueTask PresentAsync(... frame, CancellationToken ct)` — same name, same shape
- `ValueTask OnFormatChangedAsync(format, CancellationToken ct)` — same callback
  semantics for mid-stream resolution / pixel-format switches
- `IAsyncDisposable` for teardown (matching `IVideoSink`)
- A `SupportedMemoryDomains` advertisement so future GPU sinks can be
  recognized without runtime probing

#### What we deliberately do *not* mirror

- **Sinks don't own pools.** Frame-flow's decoder pulls frames from a sink-owned
  `IFramePool` because frames originate downstream-of-sink in their model.
  Periphery's pipeline is push-based: the camera produces frames into its own
  pool upstream. Sinks that produce derived frames (e.g. a converter, a
  broadcaster) own their own pools internally; pure consumer sinks don't.
  This keeps the simple sink case (just consume frames) trivial.
- **Frame primitive stays `ICameraFrame`.** No new `IVideoFrame` is introduced
  in core. The bridge wraps a `LeasedCameraFrame` as an `IVideoFrame` at the
  FrameFlow boundary. As of ADR-0035 §8b (2026-05-09), Periphery's frames
  are themselves ref-counted (`ICameraFrame.AddRef()` / `Dispose()`), so the
  bridge is closer to a pass-through than a translation — `IVideoFrame.AddRef`
  forwards directly to `ICameraFrame.AddRef`, no ref-counting promotion at
  the boundary.
- **Plane access stays `GetPlane(int)`.** More general than frame-flow's
  Y/U/V-named struct. The bridge maps it to `CpuFrameData` at the FrameFlow
  boundary.

#### What this means for ADR-0036 implementation work

When the Pipelines package ships, its sink interface should be defined in
terms a frame-flow contributor would recognize at a glance:

```csharp
public interface ICameraFrameSink : IAsyncDisposable
{
    IReadOnlyList<CameraFrameMemoryDomain> SupportedMemoryDomains { get; }
    ValueTask PresentAsync(ICameraFrame frame, CancellationToken ct);
    ValueTask OnFormatChangedAsync(CameraFormatInfo format, CancellationToken ct);
}
```

The names of `CameraFrameMemoryDomain` and `CameraFormatInfo` mirror
`FrameMemoryDomain` and `VideoFormatInfo` from frame-flow. This is not
gratuitous duplication — it's a stable shared vocabulary at the package
boundary that lets the bridge be a one-file adapter.

#### What this does *not* commit Periphery to

Periphery does not depend on frame-flow. The packages live in different
repositories with different dependency profiles (Periphery has zero
third-party native deps; frame-flow ships FFmpeg). The symmetry is at the
shape level only — both sides could be reimplemented from scratch and still
match.

---

## Goal Use Cases

These are the top-level application experiences the pipeline layer should optimize for.

### Use Case 1 — Simple camera preview loop

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .ToDisplay(view, ct);
```

### Use Case 2 — Record to a file

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .EncodeAs(H264)
    .ToFileStream(output, ct);
```

### Use Case 3 — Inference with overlay

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .RunInference(detector)
    .DrawBoxes()
    .ToDisplay(view, ct);
```

### Use Case 4 — Branch preview, inference, and recording

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .Broadcast(
        branch => branch.ToDisplay(view),
        branch => branch.RunInference(detector).ToObserver(resultsSink),
        branch => branch.EncodeAs(H264).ToFileStream(output, ct))
    .RunAsync(ct);
```

### Use Case 5 — Resize only the inference branch

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .Broadcast(
        branch => branch.Resize(640, 640)
            .RunInference(detector)
            .ToObserver(resultsSink),
        branch => branch.EncodeAs(H264)
            .ToFileStream(output, ct))
    .RunAsync(ct);
```

### Use Case 6 — Metadata-driven composition

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .RunInference(detector)
    .UseMetadata<DetectionResults>((packet, detections) =>
    {
        Console.WriteLine($"Detected {detections.Items.Count} objects.");
    })
    .DrawBoxes()
    .ToDisplay(view, ct);
```

### Use Case 7 — Reconnect-resilient live application

```csharp
await using var host = await DeviceSessionHost<CameraSession>.StartAsync(
    profile,
    (device, ct) => CameraSession.OpenAsync(device, configuration, ct: ct),
    ct: ct);

var session = await host.WaitForSessionAsync(ct);

await session
    .CaptureAsync(ct: ct)
    .AsPipeline()
    .RunInference(detector)
    .ToDisplay(view, ct);
```

---

## Package Layout Sketch

The intended layering is:

```text
Periphery
├── Periphery.Camera
├── Periphery.Camera.Pipelines
├── Periphery.Camera.Drawing          (optional future)
├── Periphery.Camera.Inference        (optional future)
├── Periphery.Camera.Encoding         (optional future)
├── Periphery.Camera.Streaming        (optional future)
└── Periphery.Camera.FFmpeg           (optional future adapter/integration)
```

`Periphery.Camera.Pipelines` owns the graph/composition model.

Domain packages contribute operators.

`Periphery.Camera` remains the lower-level frame source.

---

## Public API Sketch

The exact names may evolve, but the intended shape is roughly:

```csharp
public sealed class FramePacket : IDisposable
{
    public ICameraFrame Frame { get; }
    public FrameMetadata Metadata { get; }

    public FramePacket WithFrame(ICameraFrame replacement);
}

public sealed class FrameMetadata
{
    public void Set<T>(T value) where T : class;
    public bool TryGet<T>(out T? value) where T : class;
    public T GetRequired<T>() where T : class;
}

public sealed class FramePipeline
{
    public Task RunAsync(CancellationToken ct = default);
}

public static class CameraPipelineExtensions
{
    public static FramePipeline AsPipeline(
        this IAsyncEnumerable<LeasedCameraFrame> source);

    public static FramePipeline Transform(
        this FramePipeline pipeline,
        Func<FramePacket, CancellationToken, ValueTask<FramePacket>> transform);

    public static FramePipeline Enrich<T>(
        this FramePipeline pipeline,
        Func<FramePacket, CancellationToken, ValueTask<T>> enrich)
        where T : class;

    public static FramePipeline Observe(
        this FramePipeline pipeline,
        Func<FramePacket, CancellationToken, ValueTask> observer);

    public static FramePipeline Broadcast(
        this FramePipeline pipeline,
        params Func<FramePipeline, FramePipeline>[] branches);

    public static Task ToFileStream(
        this FramePipeline pipeline,
        Stream output,
        CancellationToken ct = default);
}
```

This sketch is intentionally minimal. The key decisions are the packet envelope, typed
metadata, extension-method operator model, and branching support.

---

## Non-Goals

This ADR does **not** require the first implementation of `Periphery.Camera` to ship a
pipeline package immediately.

It also does not lock in:

- one exact type name (`FramePipeline`, `FrameGraph`, `PacketStream`, etc.)
- one exact metadata API
- one exact scheduler/runtime strategy
- one exact set of built-in operators

The purpose of this ADR is to record the ambitious direction and define the boundaries so
the camera core can be built in a way that does not block this future layer.

---

## Consequences

### Positive

- gives the project a strong top-level application story beyond raw frame loops
- creates a clean home for inference, drawing, encoding, and streaming operators
- preserves the simplicity of the camera core by keeping the higher-level graph model
  optional
- gives extension packages a typed collaboration model via `FramePacket` and
  `FrameMetadata`

### Negative

- introduces a second abstraction layer that will need careful runtime and ownership
  design
- raises the bar for documentation because buffering and dropping semantics must remain
  explicit
- may create pressure to over-design the first implementation if not kept separate from
  ADR-0035's critical path

### Follow-up questions

- whether the pipeline runtime should be built directly on `IAsyncEnumerable<T>`,
  `Channel<T>`, a custom graph runtime, or a hybrid model
- whether packet metadata should be mutable, append-only, or copy-on-write
- whether branching should be eager and runtime-managed or merely a composition helper
- whether `FramePacket` should own exactly one frame payload or support multiple named
  image attachments later
