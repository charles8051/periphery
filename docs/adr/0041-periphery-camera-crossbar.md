---
title: "ADR-0041: Periphery.Camera.Crossbar replaces Periphery.Camera.Pipelines"
status: "Superseded"
date: "2026-05-10"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "camera", "pipelines", "crossbar", "package-rename", "api-design"]
supersedes: "0036"
superseded_by: "0042"
---

# ADR-0041: Periphery.Camera.Crossbar replaces Periphery.Camera.Pipelines

> **Note, added at the source-available release (2026-08-30).** The external
> graph substrate this record adopts is a private repository, so a public
> reader cannot follow references to it. Kept as the
> first step of the trail that ends in
> [ADR-0045](0045-substrate-independence-from-crossbar.md), which reversed it.

## Status

> This ADR's migration target (`FrameConsumer<TFrame> Consumer { get; }`
> property + package rename to `Periphery.Camera.Crossbar`) was
> drafted against Crossbar's ADR-0010 / ADR-0012 era. Crossbar's
> ADR-0014 Phase 4 (2026-05-17) then deleted `FrameConsumer<T>` and
> `FramePipeline<T>` entirely in favor of a node-and-port graph
> runtime. The migration as planned here is not achievable against
> the current substrate. ADR-0042 records the integration that
> landed instead — `ICameraFrame` extends `Crossbar.IRefCounted`
> directly; the package name `Periphery.Camera.Pipelines` is
> retained; substrate adapters (`AsSinkNode` / `AsSourceNode`) live
> alongside the unchanged `ICameraFrameSink` contract.
>
> **Nothing in this record is actionable.** The chain ran
> ADR-0036 -> ADR-0041 -> [ADR-0042](0042-periphery-crossbar-substrate-integration.md)
> -> **[ADR-0045](0045-substrate-independence-from-crossbar.md)**, and ADR-0045
> reversed the premise entirely: Periphery is substrate-independent,
> `ICameraFrame` extends nothing of Crossbar's, and the `Crossbar`
> PackageReference is gone. `Periphery.Camera.Crossbar` was never created.
> The phase tables, goal use cases, and migration steps below are preserved
> as the record of what was intended in May 2026 — read them as history, and
> execute none of them.

Supersedes [ADR-0036](0036-periphery-camera-pipelines.md). The
substantive design content of 0036 — packet envelope, typed metadata,
operator categories, branching, ownership preservation, sink-shape
parity with frame-flow — remains in force. What changes is the
package's identity: it is now a *binding* for Crossbar rather than a
self-contained pipeline runtime.

## Context

ADR-0036 (2026-05-06) introduced `Periphery.Camera.Pipelines` as a
higher-level fluent frame-processing layer over `Periphery.Camera`'s
I/O foundation. The package shipped a skeleton — sink contract +
supporting types — with the runtime planned to follow.

In the four days since, Crossbar (a separate, private repository)
has been extracted as a **vendor-neutral graph runtime** for media
pipelines. Its public surface (`FramePipeline<TFrame>`, `FramePacket<TFrame>`,
`FrameMetadata`, the `Transform`/`Enrich`/`Observe`/`Broadcast`/
`ToSinkAsync` operators, `FrameChannelOptions`,
`FrameOverflowPolicy`) is exactly the runtime ADR-0036 envisioned —
generic over `TFrame : IFrame` so consumers bind their own frame
primitives. Crossbar's substrate decisions are formalized in
Crossbar's own ADR-0001, *Pipeline substrate and ownership*.

Crossbar's existence forces a question ADR-0036 didn't have to
answer: **does `Periphery.Camera.Pipelines` still need to exist as
a distinct layer, or should consumers see Crossbar directly?**

Three concrete options:

1. **Two layers, thin Periphery facade.** `Periphery.Camera.Pipelines`
   wraps Crossbar's operators in Periphery-named extension methods
   (e.g. `cameraPipeline.Transform(...)` calls into
   `Crossbar.FramePipeline<ICameraFrame>.Transform(...)` underneath).
   Consumers see `Periphery.Camera.Pipelines` types in their code.
2. **Two layers, Periphery-specific operator vocabulary.**
   `Periphery.Camera.Pipelines` ships its own `Transform`,
   `Enrich`, `Observe`, `Broadcast` that happen to wrap Crossbar's
   but appear in IntelliSense alongside camera-specific operators.
   The wrapping creates a Periphery-flavored vocabulary distinct
   from Crossbar's.
3. **One layer, direct Crossbar exposure.** Rename
   `Periphery.Camera.Pipelines` to `Periphery.Camera.Crossbar`. The
   package binds Crossbar to camera frames (`ICameraFrame :
   Crossbar.IFrame`, sink contract extending
   `Crossbar.IFrameSink<ICameraFrame>` with format-change handling)
   and stops there. Consumers write Crossbar code; the binding
   makes camera frames flow through it.

Option 3 is what this ADR adopts. The reasons follow.

### Reason 1 — `Periphery.Camera.Pipelines` has not shipped a runtime

The package is currently a four-file skeleton:
`ICameraFrameSink`, `CameraFormatInfo`, `CameraFrameMemoryDomain`,
`NullCameraFrameSink`. The runtime — `FramePacket`, `FramePipeline`,
operator extension methods — is the work that *would* land next.
Doing that work now would mean reimplementing what Crossbar already
provides. There is no shipped surface to deprecate, only a
not-yet-built one to skip.

### Reason 2 — A Periphery facade adds learning gradient without value

If `Periphery.Camera.Pipelines.Transform(...)` and
`Crossbar.FramePipeline<TFrame>.Transform(...)` are the same
operation with different names, consumers pay a learning cost for no
gain. They look up two sets of XML doc comments, two sets of
examples; they can't paste a Periphery pipeline into a FrameFlow
pipeline (or vice versa) without renaming everything; they can't
read a Crossbar tutorial as if it applied to their Periphery code.

A facade layer is justified when it adds opinion (different defaults,
constraints, simplification). A facade that one-to-one wraps the
underlying runtime is just rename overhead.

### Reason 3 — Honest naming for what the package actually does

`Periphery.Camera.Pipelines` was the right name when the package was
going to host a self-contained pipeline runtime. Now that Crossbar
*is* the runtime, the package's job is "bind Crossbar to camera
frames." `Periphery.Camera.Crossbar` says that out loud. The
.NET ecosystem convention for integration packages is `<source>.<target>`
(e.g., `Serilog.Sinks.Console`, `Microsoft.Extensions.Hosting.Wpf`),
which puts the binding's Crossbar-target in the name.

### Reason 4 — Crossbar's API stability is already load-bearing

The argument against direct Crossbar exposure is "now Crossbar's API
stability matters for every downstream consumer, not just one
wrapper layer." This is true — but Crossbar already addresses it:
Crossbar's ADR-0001 (*Pipeline substrate and ownership*),
its SemVer policy,
and the `Microsoft.CodeAnalysis.PublicApiAnalyzers`-tracked public
surface together commit Crossbar to a stability discipline that
downstream binders can trust. We are paying that cost regardless;
adding a facade layer doesn't reduce it, only obscures it.

### Reason 5 — Cross-consumer vocabulary consistency

A consumer who learns Crossbar through Periphery can read FrameFlow
code without translation. A consumer who masters one Crossbar
binding masters all of them. This is the same property that makes
LINQ portable across collection sources: a single mental model
transfers. Periphery-flavored facades would prevent that.

## Decision

Rename `Periphery.Camera.Pipelines` to `Periphery.Camera.Crossbar`
and reposition the package as a Crossbar binding. Specifically:

### Decision 1 — `Periphery.Camera.Crossbar` is a Crossbar binding

The package's job is to make `Periphery.Camera`'s frame primitives
flow through Crossbar's runtime. It contains:

- The `ICameraFrameSink` interface (extending
  `Crossbar.IFrameSink<ICameraFrame>` with `OnFormatChangedAsync`).
- `CameraFormatInfo` — the camera-specific format envelope that
  Crossbar's substrate cannot carry.
- `NullCameraFrameSink` — terminal-sink reference implementation.
- The `CameraSession.AsCrossbarPipeline()` extension that yields a
  `Crossbar.FramePipeline<ICameraFrame>`.
- Camera-specific operators when they are written:
  `RunInference(IObjectDetector)`, `DrawBoxes()`, `EncodeAs(...)`,
  `ToFileStream(...)`, `ToDisplay(view)`, etc. — implemented as
  extension methods on `Crossbar.FramePipeline<ICameraFrame>`.

The package does **not** own the runtime, the packet envelope, the
metadata bag, or the substrate operators. Those live in Crossbar.

### Decision 2 — `ICameraFrame` formally implements `Crossbar.IFrame`

`Periphery.Camera.ICameraFrame` already has `Width`, `Height`, and
`Timestamp` (per ADR-0035). Add the `: Crossbar.IFrame` declaration.
The interface gains no new members. The change is mechanical:
formal conformance, no consumer impact.

`Periphery.Camera` takes a transitive Crossbar dependency through
this. The dependency is unavoidable if the binding is to work; the
alternative is duplicate `IFrame`-shaped declarations on both sides
and a runtime cast at the binding boundary, which is worse.

### Decision 3 — `ICameraFrameSink` is a standalone interface exposing a `FrameConsumer<ICameraFrame>`

> **Update (Crossbar ADR-0010 / ADR-0012, 2026-05-15):** the shape
> originally proposed here (`ICameraFrameSink : Crossbar.IFrameSink<ICameraFrame>`)
> is obsolete. Crossbar ADR-0010 separated the dataflow facet
> (`FrameConsumer<TFrame>` delegate) from the resource/lifecycle facet
> (the library-specific sink interface) and ultimately removed
> `IFrameSink<TFrame>` from the substrate entirely. Crossbar ADR-0012
> simultaneously removed `SupportedMemoryDomains` — domain conversion
> is now an explicit pipeline operator, not a substrate-level
> advertisement. The current shape is documented below.

The current `Periphery.Camera.Pipelines.ICameraFrameSink`:

```csharp
public interface ICameraFrameSink : IAsyncDisposable
{
    IReadOnlyList<CameraFrameMemoryDomain> SupportedMemoryDomains { get; }
    ValueTask PresentAsync(ICameraFrame frame, CancellationToken ct);
    ValueTask OnFormatChangedAsync(CameraFormatInfo format, CancellationToken ct);
}
```

The new shape (post-migration, aligned with FrameFlow's `IVideoSink` /
`IAudioSink` per Crossbar ADR-0010 Phase 3):

```csharp
// Periphery.Camera.Crossbar (post-rename)
public interface ICameraFrameSink : IAsyncDisposable
{
    /// Dataflow facet — wired into Crossbar pipelines via
    /// `pipeline.ToSink(cameraSink.Consumer)`.
    Crossbar.FrameConsumer<ICameraFrame> Consumer { get; }

    /// Periphery-specific resource facet.
    ValueTask OnFormatChangedAsync(CameraFormatInfo format, CancellationToken ct);
}
```

Each implementer wires `Consumer = PresentAsync` (cached delegate) in
its constructor, keeping a public `PresentAsync` method for direct
invocation while exposing the substrate-facing `FrameConsumer<T>`.
`SupportedMemoryDomains` and the inheritance from
`Crossbar.IFrameSink<TFrame>` both disappear — the substrate has no
sink interface anymore, only the consumer delegate.

### Decision 4 — `CameraFrameMemoryDomain` is replaced by `Crossbar.FrameMemoryDomain`

> **Update (Crossbar ADR-0012, 2026-05-15):** `IFrameSink<TFrame>.SupportedMemoryDomains`
> was removed from Crossbar entirely — the substrate no longer
> advertises or negotiates per-sink memory domains. Camera frame types
> that surface a domain attribute (e.g.
> `ICameraFrame.MemoryDomain`) should still adopt
> `Crossbar.FrameMemoryDomain` for diagnostic / branching purposes;
> there is no longer a sink-side `SupportedMemoryDomains` declaration
> to migrate.

The current `CameraFrameMemoryDomain` enum has a single value
(`Cpu`) and was deliberately shaped to mirror frame-flow's
`FrameMemoryDomain`. `Crossbar.FrameMemoryDomain` has the same shape
for the same reason. Maintaining a Periphery-specific enum that
mirrors a Crossbar enum is duplication.

`CameraFrameMemoryDomain` is removed. Camera frames expose
`Crossbar.FrameMemoryDomain` directly on the frame type for inspection
and branching. Future GPU-domain extensions follow Crossbar's evolution
(per Crossbar ADR-0001 §4 — the enum becomes extensible via a
capability-handle layer when the first GPU consumer lands).

### Decision 5 — Operator vocabulary is Crossbar's

Consumers compose pipelines using Crossbar's operator extension
methods directly:

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()                    // Periphery.Camera.Crossbar
    .Transform(/* ... */)                    // Crossbar
    .Enrich<ICameraFrame, DetectionResults>(/* ... */) // Crossbar
    .Broadcast(/* ... */)                    // Crossbar
    .ToSinkAsync(sink, ct);                  // Crossbar
```

Camera-specific operators (`RunInference`, `DrawBoxes`, `EncodeAs`,
`ToDisplay`) are extension methods on
`Crossbar.FramePipeline<ICameraFrame>` that compose with the
Crossbar operators naturally:

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .RunInference(detector)                  // Periphery.Camera.Crossbar
    .DrawBoxes()                             // Periphery.Camera.Crossbar
    .ToDisplay(view, ct);                    // Periphery.Camera.Crossbar
```

There is no facade between consumer code and Crossbar.

### Decision 6 — Camera-specific operators ship initially in `Periphery.Camera.Crossbar`

Camera-specific operators (`RunInference`, `DrawBoxes`, encoding,
streaming) are scope from ADR-0036 §"Package Layout Sketch". The
sketch envisioned splitting them into focused subpackages
(`Periphery.Camera.Inference`, `Periphery.Camera.Drawing`, …). That
split remains the eventual target; it is appropriate when a heavy
dependency forces it (ONNX Runtime is ~50 MB and shouldn't be
mandatory for a consumer that only wants display).

Until that pressure materializes, the operators ship in
`Periphery.Camera.Crossbar`. Splitting is an additive evolution
(per the SemVer policy: new packages are minor changes; moving
extension methods between packages can be done with type-forwarding
attributes). Defer it.

### Decision 7 — Goal use cases from ADR-0036 are preserved

The seven goal use cases from ADR-0036 still drive design. They are
restated below using the new vocabulary; the only change is the
namespace surface consumers reference.

### Decision 8 — Migration is staged, not a flag day

The rename touches roughly 15–20 files across `Periphery.Camera`,
`Periphery.Camera.Avalonia`, examples, tests, and docs. It is gated
on Crossbar reaching 0.1.0 (a tagged stable release, with the public
surface frozen under SemVer). Until that gate clears, the existing
`Periphery.Camera.Pipelines` package name remains; the rename PR is
queued.

A per-file migration plan accompanied this ADR. It was deleted rather than
kept, because the migration it sequenced never ran and ADR-0045 removed its
premise; the phase table above is what remains of the intended shape.

## Goal Use Cases (restated for the new vocabulary)

These are the same scenarios from ADR-0036 §"Goal Use Cases",
rewritten against `Periphery.Camera.Crossbar` + `Crossbar`. The
shape stays the same; the name surface does not.

### Use Case 1 — Simple camera preview loop

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .ToDisplay(view, ct);
```

### Use Case 2 — Record to a file

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .EncodeAs(H264)
    .ToFileStream(output, ct);
```

### Use Case 3 — Inference with overlay

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .RunInference(detector)
    .DrawBoxes()
    .ToDisplay(view, ct);
```

### Use Case 4 — Branch preview, inference, and recording

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .Broadcast(
        FrameChannelOptions.LowLatencyDropIncoming,
        branch => branch.ToDisplay(view),
        branch => branch.RunInference(detector).ToObserver(resultsSink),
        branch => branch.EncodeAs(H264).ToFileStream(output, ct))
    .RunAsync(ct);
```

Note `FrameChannelOptions` is now part of the surface — a Crossbar
type, exposed directly. Per-branch backpressure becomes explicit
rather than implicit.

### Use Case 5 — Resize only the inference branch

```csharp
await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .Broadcast(
        FrameChannelOptions.LowLatencyDropIncoming,
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
    .AsCrossbarPipeline()
    .Enrich<ICameraFrame, DetectionResults>((packet, ct) =>
        detector.DetectAsync(packet.Frame, ct))
    .Observe((packet, ct) =>
    {
        if (packet.Metadata.TryGet<DetectionResults>(out var detections))
            Console.WriteLine($"Detected {detections!.Items.Count} objects.");
        return ValueTask.CompletedTask;
    })
    .DrawBoxes()
    .ToDisplay(view, ct);
```

`Enrich<TFrame, TMeta>` and the typed `Metadata.TryGet<T>` come from
Crossbar; `DrawBoxes` and `ToDisplay` come from
`Periphery.Camera.Crossbar`.

### Use Case 7 — Reconnect-resilient live application

```csharp
await using var host = await DeviceSessionHost<CameraSession>.StartAsync(
    profile,
    (device, ct) => CameraSession.OpenAsync(device, configuration, ct: ct),
    ct: ct);

var session = await host.WaitForSessionAsync(ct);

await session
    .CaptureAsync(ct: ct)
    .AsCrossbarPipeline()
    .RunInference(detector)
    .ToDisplay(view, ct);
```

Identical to ADR-0036 §"Use Case 7" save the `AsCrossbarPipeline()`
entry point.

## Package Layout

```text
Periphery
├── Periphery.Camera                  // I/O foundation (ADR-0035)
│                                     // ICameraFrame : Crossbar.IFrame
├── Periphery.Camera.Crossbar         // Binding + camera-specific operators
│   ├── ICameraFrameSink : Crossbar.IFrameSink<ICameraFrame>
│   ├── CameraFormatInfo
│   ├── NullCameraFrameSink
│   ├── CameraSession.AsCrossbarPipeline()
│   ├── RunInference / DrawBoxes / EncodeAs / ToDisplay / ...
│   └── (extension methods on Crossbar.FramePipeline<ICameraFrame>)
├── Periphery.Camera.Avalonia         // Avalonia-specific sinks
│                                     // CameraPreview : ICameraFrameSink
└── (future, when heavy deps force a split)
    ├── Periphery.Camera.Crossbar.Inference   // ONNX-Runtime-backed RunInference
    ├── Periphery.Camera.Crossbar.Encoding    // FFmpeg-backed EncodeAs
    └── Periphery.Camera.Crossbar.Drawing     // ImageSharp-backed DrawBoxes
```

External (in their own repos):

```text
Crossbar                              // Vendor-neutral substrate
├── (Crossbar runtime, operators)
└── Crossbar.Rx                       // Rx interop (separate assembly)

frame-flow
├── FrameFlow.Media                   // IVideoFrame : Crossbar.IFrame
├── FrameFlow.Crossbar                // Binding + media-playback operators
└── FrameFlow.Decoding / Audio / ...
```

## Migration plan summary

The full migration is staged as:

| Phase | Gate | Scope |
|---|---|---|
| 0 | Crossbar 0.1.0 tagged | External — must clear before Phase 1. |
| 1 | Phase 0 done | Rename csproj + namespace. `ICameraFrame : Crossbar.IFrame`. Sink interface extends Crossbar's. Drop `CameraFrameMemoryDomain`. |
| 2 | Phase 1 done | Update `Periphery.Camera.Avalonia`, examples, tests, and docs to reference the new package name. |
| 3 | Phase 2 done | Replace `BroadcastFanOut` in the multicast example with `Crossbar.Broadcast`. Per-branch error pattern moves to Crossbar's reference Broadcast implementation. |
| 4 | Phase 3 done | ADR-0036 status flips to `Superseded`. CHANGELOG entry. Release as a minor version of `Periphery.Camera.Crossbar` (new package id; old package is yanked or deprecated). |

Per-file scope, ordering, and rollback lived in the companion migration plan,
deleted along with the migration itself. None of these phases were executed.

## Consequences

### Positive

- One operator vocabulary across all Crossbar consumers (Periphery,
  FrameFlow, future).
- The package's name is honest about what it does.
- No facade layer to maintain or document.
- New camera-specific operators land as Crossbar extension methods,
  composing naturally with all built-in operators without wrapper
  boilerplate.
- Consumers learning one Crossbar binding effectively learn the
  others.

### Negative

- Crossbar's API stability is now load-bearing for every downstream
  Periphery.Camera consumer that uses pipelines. Mitigated by
  Crossbar ADR-0001, the SemVer policy, and PublicApiAnalyzers-
  tracked public surface — but Crossbar churn now has a visible
  blast radius.
- Consumers see Crossbar types (`FramePacket<ICameraFrame>`,
  `FrameMetadata`, `FrameChannelOptions`) directly in their code.
  No Periphery-flavored simplification is available. Consumers who
  prefer a high-level Periphery facade have to write one
  application-side.
- ADR-0036's package-layout sketch is partially superseded — the
  fluent API surface lives in Crossbar, not Periphery. The
  remaining Periphery layout intent (focused subpackages for
  inference / drawing / encoding) is preserved.
- Migration is a staged refactor with measurable surface area
  (~15–20 files). Not large, not trivial.

### Neutral

- The four-file skeleton currently in `Periphery.Camera.Pipelines`
  becomes the four-file skeleton in `Periphery.Camera.Crossbar`,
  with shape changes to align with Crossbar's contracts. No
  shipped public API is broken because none has shipped yet.

## Alternatives considered

### A. Keep `Periphery.Camera.Pipelines` and depend on Crossbar internally

Two layers, with Periphery wrapping Crossbar in extension methods.
Consumers see `Periphery.Camera.Pipelines.Transform(...)` etc.

Rejected for the reasons in §"Reason 2" — facade with no opinion
adds learning cost without value. If the wrapper later acquires
opinion (different defaults, simplification), the rename can be
revisited.

### B. Build a runtime in `Periphery.Camera.Pipelines`, ignore Crossbar

Continue ADR-0036 as planned, with Periphery owning a runtime
parallel to Crossbar's. Rejected because it duplicates the work
Crossbar exists to share. The whole reason Crossbar was extracted is
that Periphery's pipeline mechanics and frame-flow's are ~80% the
same; rebuilding within Periphery defeats the extraction.

### C. Make `Crossbar` a hard dependency of `Periphery.Camera` core

Push `ICameraFrame : Crossbar.IFrame` into core but leave
`Periphery.Camera.Pipelines` as the public layer. Rejected because
it forces every camera consumer (including ones that never use
pipelines) into a transitive Crossbar dependency. Opt-in pipelines
via `Periphery.Camera.Crossbar` keeps the core lean.

The chosen option still puts `ICameraFrame : Crossbar.IFrame` in
core (Decision 2) — but only the interface declaration, which is a
minimal one-line dependency. Consumers using the core for capture
without pipelines pay only the cost of the type-level reference.

### D. Drop `ICameraFrameSink` and use `Crossbar.IFrameSink<ICameraFrame>` directly

Eliminate the Periphery-specific sink interface entirely. Consumers
implement `Crossbar.IFrameSink<ICameraFrame>`. Format-change
notifications happen via metadata or a separate observer.

Rejected because format changes are camera-specific and
type-asymmetric — a pixel-format switch matters to a sink that
caches conversion state but not to one that only reads dimensions.
The library-extension pattern (Crossbar provides the substrate
sink; libraries extend with format-change handling) is exactly what
Crossbar's README §"Out of scope (deliberately)" anticipates.

## Cross-references

- [ADR-0036](0036-periphery-camera-pipelines.md) — superseded by
  this ADR. Substantive design content (packet envelope, typed
  metadata, branching, ownership, sink-shape parity) is preserved
  through Crossbar.
- [ADR-0035 §8b](0035-periphery-camera.md) — refcounted
  `ICameraFrame.AddRef()` / `Dispose()`, the load-bearing model
  that makes Crossbar's `Broadcast` operator zero-copy across
  camera frames.
- [ADR-0040](0040-camera-ergonomic-roadmap.md) — camera ergonomic
  roadmap; the inference / overlay / encoding work plans against
  this ADR's package layout.
- Crossbar ADR-0001, *Pipeline substrate and ownership* — the substrate
  decisions Periphery.Camera.Crossbar would have bound against.
- Crossbar's SemVer policy — the stability commitment that made direct
  Crossbar exposure acceptable.

Both live in a separate, private repository. The per-file migration plan that
stood here was deleted with this ADR's supersession: the migration never ran.
