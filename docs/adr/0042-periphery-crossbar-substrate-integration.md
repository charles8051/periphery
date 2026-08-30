---
title: "ADR-0042: Periphery integrates the Crossbar substrate directly via ICameraFrame : IRefCounted"
status: "Superseded"
date: "2026-05-18"
authors: "@charles8051 (decision)"
tags: ["architecture", "decision", "camera", "pipelines", "crossbar", "substrate", "refcounting"]
supersedes: "0036, 0041"
superseded_by: "0045"
---

# ADR-0042: Periphery integrates the Crossbar substrate directly via ICameraFrame : IRefCounted

> **Note, added at the source-available release (2026-08-30).** The external
> graph substrate this record integrates is a private repository, so a public
> reader cannot follow references to it. This ADR
> made `ICameraFrame` inherit the substrate's contracts; it does **not**
> describe the interface today. The reversal is
> [ADR-0045](0045-substrate-independence-from-crossbar.md), and that is where
> the current API's rationale lives.

## Status

**Superseded by [ADR-0045](0045-substrate-independence-from-crossbar.md)**
(2026-05-22), which reversed this decision four days after it landed:
`ICameraFrame` no longer extends `Crossbar.IFrame` / `Crossbar.IRefCounted`,
and the `Crossbar` PackageReference is gone. Frame ownership is Periphery's
own contract. Read this record for why the integration was attempted, not for
what Periphery does now.

Supersedes [ADR-0036](0036-periphery-camera-pipelines.md) and
[ADR-0041](0041-periphery-camera-crossbar.md). Both predecessors
described an in-progress migration toward a Crossbar shape that
Crossbar itself then revised before the migration landed. This ADR
records what was actually built.

## Context

ADR-0036 (2026-05-06) introduced `Periphery.Camera.Pipelines` as a
sink-contract package, with the intent that a future Crossbar binding
would map onto its surface. ADR-0041 (2026-05-10) replaced that intent
with a concrete plan: rename to `Periphery.Camera.Crossbar`, swap
`PresentAsync` for a `FrameConsumer<TFrame> Consumer { get; }` property
exposing the ADR-0010 consumer delegate, drop
`SupportedMemoryDomains` in line with Crossbar ADR-0012.

The execution of ADR-0041 was blocked on "consumer demand" — no
periphery user actually needed the substrate integration, so the
migration sat in the deferred-work backlog.

In the interval, **Crossbar landed its own ADR-0014 Phase 4**
(2026-05-17). That refactor deleted `FramePipeline<T>`,
`FrameConsumer<T>`, and the `IFrameSink<T>` ancestry — the entire
package shape ADR-0041's migration plan targeted. What replaced it:

- Crossbar's substrate is now a **node-and-port graph runtime**:
  `Graph`, `SourceNode<TOut>`, `SinkNode<TIn>`,
  `OperatorNode<TIn,TOut>`, `StorageNode<T>` (fan-out),
  `JoinNode<TIn1,TIn2,TOut>` (fan-in).
- Items flowing through the graph implement **`Crossbar.IRefCounted`**:
  an `IDisposable` with an `AddRef() -> IRefCounted` method. The
  substrate AddRefs and Disposes items at every channel boundary.
- Per-edge behavior is configured via `EdgeOptions` (shape, cadence,
  capacity, overflow, underflow).

Periphery's `ICameraFrame` already had the semantic content of
`IRefCounted` (`AddRef()` returning `ICameraFrame`, `Dispose()`
balancing the refcount). The two interfaces differed only in
`AddRef`'s declared return type.

When a real demand finally arrived — porting the
`Periphery.Camera.Inference.Example`'s `.AsPipeline().ToSinkAsync(...)`
call site, broken by Phase 4's deletion of those types — both ADR-0036
and ADR-0041 were stale references to plans that no longer fit the
post-Phase-4 substrate. This ADR records the integration that actually
landed instead.

## Decision

Three coordinated changes:

### 1. `ICameraFrame` extends both `Crossbar.IFrame` and `Crossbar.IRefCounted`

```csharp
public interface ICameraFrame : Crossbar.IFrame, Crossbar.IRefCounted
{
    // Width, Height, Timestamp inherited from Crossbar.IFrame.
    // Dispose inherited from IDisposable (via both bases).
    new ICameraFrame AddRef();  // hides IRefCounted.AddRef with covariant return

    // Camera-specific members:
    CameraPixelFormat PixelFormat { get; }
    int PlaneCount { get; }
    bool IsContiguous { get; }
    ReadOnlyMemory<byte> ContiguousBuffer { get; }
    CameraPlane GetPlane(int index);
}
```

Concrete implementers (`LeasedCameraFrame`, `OwnedCameraFrame`, test
`FakeFrame`s) gain one line each:

```csharp
Crossbar.IRefCounted Crossbar.IRefCounted.AddRef() => AddRef();
```

The covariant return on the camera-typed `AddRef` keeps existing
camera-side callers strongly typed against `ICameraFrame`; the
explicit `IRefCounted.AddRef()` forwards to the same body.

### 2. `Periphery.Camera.Pipelines` ships substrate adapters

Two static extension classes:

- `CameraFrameSinkAdapters.AsSinkNode(this ICameraFrameSink)` — wraps
  any camera-frame sink as a `SinkNode<ICameraFrame>`. The body
  AddRefs the substrate's ref before handing it to
  `ICameraFrameSink.PresentAsync(frame, ct)` (the sink owns its ref
  per the `ICameraFrameSink` contract; the substrate disposes its own
  ref after the body returns).
- `CameraSourceAdapters.AsSourceNode<T>(this IAsyncEnumerable<T>)` —
  wraps any `IAsyncEnumerable<T>` (where `T : class, ICameraFrame`) as
  a `SourceNode<ICameraFrame>`. The enumerator is created lazily with
  the substrate's cancellation token and disposed via the source
  node's `Cleanup` hook on EOS / cancellation / exception.

These are the only two adapter shapes a camera consumer needs to
build a full Crossbar graph end-to-end.

### 3. `ICameraFrameSink` stays — same shape as before

`ICameraFrameSink` keeps its `PresentAsync(ICameraFrame, CancellationToken)`
+ `OnFormatChangedAsync(CameraFormatInfo, CancellationToken)` +
`SupportedMemoryDomains` shape from ADR-0036. That shape already matched
frame-flow's post-Phase-4 `IVideoSink`; no migration needed. The contract
isn't a `SinkNode` itself — it's a higher-level type that the substrate
adapter bridges onto a `SinkNode`. That separation lets sinks keep
lifecycle (`IAsyncDisposable`), format-change notifications, and
memory-domain advertisement out of the per-frame hot path.

## Consequences

### What the integration buys

- **Direct flow.** `ICameraFrame` rides Crossbar graphs without a
  wrapper type. The substrate sees the same frame object the camera
  pool leased; refcount math is one type's responsibility, not split
  between a wrapper and a wrapped frame.
- **One canonical pattern.** Periphery consumers building real camera
  → ⟨operators⟩ → sink graphs use the same primitives as
  frame-flow consumers building decoder → ⟨operators⟩ → sink graphs.
  The cross-framework `FrameFlow.Camera` bridge (still in
  frame-flow's repository, per ADR-0036's plugin-to-framework
  convention) becomes a thin alignment of frame types, not a
  substrate boundary.
- **Substrate-grade error / cancellation propagation.** Per-node pump
  tasks, linked cancellation tokens, fault propagation, and per-edge
  buffering — all from Crossbar's `Graph` for free.

### What the integration changes from earlier plans

- **No package rename.** `Periphery.Camera.Pipelines` stays.
  ADR-0041's proposed `Periphery.Camera.Crossbar` rename was tied to
  the `FrameConsumer<T>` contract change; with the substrate shaped
  differently, the rename's motivation evaporates and the existing
  package name remains descriptive (it ships pipeline-adjacent types
  — sink contract + substrate adapters).
- **`SupportedMemoryDomains` stays on `ICameraFrameSink`.** Crossbar
  ADR-0012 removed memory-domain advertisement from the substrate
  surface, but that ADR was about the substrate not negotiating
  compatibility on the consumer's behalf — it didn't speak to whether
  a sink interface can still expose what it accepts. Periphery's sink
  contract keeps the property because real camera consumers (Avalonia
  preview, OpenCV inference) make decisions based on it. The
  substrate doesn't read it; only the sink-aware caller does.
- **`CameraFrameMemoryDomain` enum stays.** Periphery-defined, used by
  `SupportedMemoryDomains`. Crossbar's `FrameMemoryDomain` (if it
  arrives) and Periphery's enum can coexist — the camera-frame types
  expose Periphery's enum because that's what callers reading
  `frame.MemoryDomain` care about.

### What's deferred to a future consumer

- **`FrameFlow.Camera` bridge.** A real frame-flow consumer wiring a
  camera as a `MediaSource` is the next demand point. The shape
  symmetry (`ICameraFrameSink.PresentAsync` ⇔ `IVideoSink.PresentAsync`,
  `ICameraFrame` ⇔ `IVideoFrame`) makes this a small package; it
  lives in frame-flow's repository per the plugin-to-framework
  convention.
- **Backpressure-aware multicast.** Crossbar's `StorageNode<T>`
  provides 1→N fan-out with bounded per-output channels, but its
  semantics (backpressure, channel-level drop, single-threaded
  per-branch bodies) differ from the multicast demo's fire-and-forget
  + per-branch error isolation. The demo retains its custom
  `BroadcastFanOut` helper; consumers wanting substrate-shaped
  fan-out wire `StorageNode` directly. See the multicast example's
  README for the contrast.
- **Per-frame inference operators in the pipeline package.** Real
  consumers will want
  `OperatorNode<ICameraFrame, ICameraFrame>`-style transforms (color
  conversion, resize, format normalize) packaged for reuse. Adding
  them is mechanical once a consumer needs them.

### Backward compatibility

Periphery had no external consumers at the time of this decision, and no
stability commitment. Breaking changes are landed inline:

- `Periphery.Camera` Crossbar PackageReference bumped from `0.1.0` to
  `0.1.2-alpha.*` (matches frame-flow's wildcard against the
  local-feed alpha track).
- `Periphery.Camera.Pipelines` gains a Crossbar PackageReference.
- Camera-side callers of `frame.AddRef()` continue to compile (return
  type unchanged at the camera surface).
- Crossbar-side callers using `IRefCounted.AddRef()` get the
  forwarding implementation on every camera-frame type.

## Alternatives considered

1. **Wrapper type (`CameraFrameRef : IRefCounted` boxing `ICameraFrame`),
   like frame-flow's `VideoFrameRef`.** Less invasive — no change to
   `ICameraFrame` or implementers. Rejected because (a) periphery
   doesn't have frame-flow's "one-shot frames that reject AddRef"
   constraint (`LeasedCameraFrame` is always refcountable), so the
   wrapper's `Detach()` machinery would be dead weight, and (b) every
   camera consumer would have to think about "domain frame vs
   substrate ref" — extra cognitive load for no payoff.

2. **Generic `IRefCounted<T>` instead of `IRefCounted`.** Crossbar's
   substrate uses the non-generic `IRefCounted` because the runtime
   doesn't need to know the item type for refcount bookkeeping;
   making it generic would push a type parameter through every
   substrate primitive without adding expressiveness. Out of scope.

3. **Stop publishing Periphery sink semantics through `ICameraFrameSink`
   and just expose `SinkNode<ICameraFrame>` factories.** Conflates two
   layers: the sink-contract concerns (lifecycle, format-change
   notifications, memory-domain advertisement) and the substrate
   per-frame routing. `ICameraFrameSink` is the right home for the
   former; `SinkNode` is the right home for the latter. The adapter
   bridges them.

4. **Keep ADR-0041's package rename + `FrameConsumer<T>` shape.** Both
   refer to types Crossbar deleted in Phase 4. Not implementable
   against the current substrate.

## Files touched

| Layer | File | Change |
|---|---|---|
| Camera core | `src/Periphery.Camera/ICameraFrame.cs` | Extends `Crossbar.IFrame, Crossbar.IRefCounted`; covariant `AddRef`. |
| Camera core | `src/Periphery.Camera/LeasedCameraFrame.cs` | Explicit `IRefCounted.AddRef()` forwarding. |
| Camera core | `src/Periphery.Camera/OwnedCameraFrame.cs` | Same. |
| Camera core | `src/Periphery.Camera/Periphery.Camera.csproj` | Crossbar pin → `0.1.2-alpha.*`. |
| Pipelines | `src/Periphery.Camera.Pipelines/CameraFrameSinkAdapters.cs` | New: `AsSinkNode()` extension. |
| Pipelines | `src/Periphery.Camera.Pipelines/CameraSourceAdapters.cs` | New: `AsSourceNode()` extension. |
| Pipelines | `src/Periphery.Camera.Pipelines/Periphery.Camera.Pipelines.csproj` | Adds Crossbar PackageReference. |
| Pipelines | `src/Periphery.Camera.Pipelines/README.md` | Rewritten — substrate integration. |
| Inference example | `examples/Periphery.Camera.Inference.Example/MainWindow.axaml.cs` | `.AsPipeline().ToSinkAsync(...)` → `new Graph().Connect(...).RunAsync(ct)`. |
| Inference example | `examples/Periphery.Camera.Inference.Example/Yolo/Yolov8InferenceCameraPreview.cs` | Doc-comment refresh. |
| Inference example | `examples/Periphery.Camera.Inference.Example/README.md` | Architecture diagram + pipeline snippet updated. |
| Multicast example | `examples/Periphery.Camera.Multicast.Example/BroadcastFanOut.cs` | Doc-comment refresh explaining why a custom helper exists. |
| Multicast example | `examples/Periphery.Camera.Multicast.Example/README.md` | Notes on substrate `StorageNode` vs this demo's fire-and-forget. |
| Tests | `tests/Periphery.Camera.Pipelines.Tests/Fakes/FakeFrame.cs` | Explicit `IRefCounted.AddRef()`. |
| Tests | `tests/Periphery.Camera.Tests/Fakes/FakeFrame.cs` | Same. |
| ADR | `docs/adr/0036-periphery-camera-pipelines.md` | (Header only — superseded note.) |
| ADR | `docs/adr/0041-periphery-camera-crossbar.md` | (Header only — superseded note.) |
