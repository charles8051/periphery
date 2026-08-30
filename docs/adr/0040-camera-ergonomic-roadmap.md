---
title: "ADR-0040: Camera ergonomic roadmap — selectors, sinks, builder, and the line to pipelines"
status: "Accepted"
status_note: "Shipped - `CameraFormatSelectors`, `CameraFrameSinks`, `CameraSessionBuilder`. The line to pipelines was drawn differently: `Periphery.Camera.Pipelines` never shipped, and the substrate coupling was reversed by [ADR-0045](0045-substrate-independence-from-crossbar.md)."
date: "2026-05-08"
authors: "@charles8051"
tags: ["architecture", "decision", "camera", "api-design", "roadmap"]
supersedes: ""
superseded_by: ""
---

# ADR-0040: Camera ergonomic roadmap — selectors, sinks, builder, and the line to pipelines

## Context

ADR-0035 defines `Periphery.Camera` as the camera I/O foundation. ADR-0036
defines `Periphery.Camera.Pipelines` as the fluent processing layer that sits
on top of it. Between those two layers is a gap: ordinary capture-and-save
workflows that should not require pipelines, but that today force every caller
to hand-roll the same boilerplate.

The reference example illustrates this. `CaptureCommand` in
`examples/Periphery.Camera.Example` contains roughly:

- ~30 lines that legitimately belong in a CLI (argparse, ctrl-C handler,
  console progress, exit codes) — these are not the problem;
- a `ChooseFormat` helper (~25 lines) that filters and ranks
  `CameraSnapshot.Formats` — every consumer of this library would write
  approximately this same helper;
- a save-frame block that picks `.jpg` vs `.raw`, builds a filename, and
  copies bytes to disk — also library-shaped, not CLI-shaped.

Format selection and frame sinking are not pipeline concerns. They do not
involve graph topology, branching, metadata propagation, encoding, or
backpressure-policy negotiation. They are the ergonomic primitives that should
exist directly on the camera core so that:

- the simple workflow ("capture N frames to a folder, look at them") is
  one call, not twenty lines, and
- pipelines (ADR-0036) remain reserved for workflows that genuinely need a
  graph runtime (preview + inference + record + stream), not as the only path
  out of awkward primitives.

The decision worth making explicitly is **what counts as core ergonomics
versus what counts as a pipeline concern**, because that line is currently
ambiguous and is causing the example bulk.

## Decision

Stage the camera ergonomics work in five layers and place the boundary
between core and pipelines deliberately. Each layer is independently
shippable and useful in isolation.

### Decision 1 — Format selectors live in core

Filtering and ranking `CameraFormat` lists is solved with LINQ-style
extension methods on `IEnumerable<CameraFormat>`. The minimum surface:

- filters: `WithPixelFormat`, `WithAnyPixelFormat`, `WithinBox`,
  `AtLeastResolution`, `AtLeastFrameRate`
- orderers: `ByHighestArea`, `ByHighestFrameRate`, plus matching
  `ThenBy…` variants
- preference: `PreferPixelFormat` (stable two-tier ordering for
  fallback selection)

These are pure functions over a list. They introduce no new abstraction, no
new lifecycle, and no hidden state. They live next to `CameraFormat` in
`Periphery.Camera`.

### Decision 2 — Frame sinks that do not encode live in core

The following sinks are byte-level and require no codec or muxer:

- `SaveToDirectoryAsync(directory, options, ct)` — writes
  `ContiguousBuffer` to one file per frame. File extension is `.jpg` for
  MJPEG (it is already JPEG-encoded), `.raw` for everything else with the
  width, height, and pixel format encoded in the filename so a viewer can
  interpret it. No image encoding step.
- `WriteContiguousToAsync(stream, ct)` — concatenates contiguous frame
  bytes into a destination stream (a memory stream, a pipe, a file).
- `ToOwnedAsync()` — promotes leased frames to owned frames at the
  enumerator boundary so downstream consumers can outlive the capture loop.

These are implemented as extension methods on `IAsyncEnumerable<ICameraFrame>`
(covariant over `LeasedCameraFrame` and `OwnedCameraFrame`). They live in
`Periphery.Camera`. The lease-disposal contract from ADR-0035 §8 is
preserved: every sink that consumes leased frames disposes them as it goes.
`ToOwnedAsync` is the single explicit promotion point.

### Decision 3 — Encoding-bearing sinks belong above core

Anything that decodes pixels, re-encodes pixels, muxes containers, or speaks
a network protocol is **not** core. That includes:

- JPEG re-encoding from raw pixel formats
- H.264 / H.265 / AV1 encoding
- MP4 / Matroska / fragmented MP4 muxing
- RTSP / RTMP / WebRTC / SRT transport

These belong in `Periphery.Camera.Pipelines` (per ADR-0036) or in a
dedicated companion package such as `Periphery.Camera.Encoding`. The
distinction is concrete and testable: a sink either reads `ContiguousBuffer`
as opaque bytes (core) or interprets pixels (above core).

### Decision 4 — Fluent builder is additive over the existing records

A discoverable convenience path of the form:

```text
CameraSession.For(device).PreferMjpeg().MaxResolution(1280, 720).OpenAsync(ct)
```

is added as a thin layer over `CameraConfiguration` and `CameraSessionOptions`
records. The records remain the source of truth — the builder calls
selectors internally, materializes a record, and forwards to the existing
`CameraSession.OpenAsync(DeviceInfo, CameraConfiguration, …)` entry point.

The builder is additive. It does not replace, deprecate, or shadow the
record-shaped construction path that ADR-0035 §"Golden core shape" already
established as canonical.

### Decision 4a — The builder is the home for snapshot-aware delegates

The builder is the formal answer to "let me configure with a delegate."
Specifically, it exposes a `UseFormat` escape hatch that takes the
discovered `CameraSnapshot` and returns the chosen `CameraFormat`:

```text
await CameraSession.For(device)
    .UseFormat(snap => snap.Formats
        .WithPixelFormat(CameraPixelFormat.Mjpeg)
        .WithinBox(1280, 720)
        .ByHighestArea()
        .First())
    .WithSessionOptions(o => o with { BufferCount = 4 })
    .OpenAsync(ct);
```

This is the factory-delegate variant of the .NET options pattern (the same
shape used by `AddDbContext((sp, opt) => …)`) and it composes with the
builder's other concerns (`CameraSessionOptions`, `CameraOpenOptions`)
without spawning new top-level overloads.

`UseFormat` is the structural answer to "I need full control over format
selection for snapshot-dependent reasons." Anything more sophisticated —
asynchronous policy lookups, reading device controls before deciding,
correlating with external configuration — is supported by an async overload
`UseFormat(Func<CameraSnapshot, CancellationToken, ValueTask<CameraFormat>>)`.

### Decision 4b — Reject `Action<TOptions>` and standalone factory overloads on `OpenAsync`

The classic configure-knobs variant of the options pattern
(`Action<TOptions> configure`) is **not** added. `CameraConfiguration` is
not a knob set — its load-bearing field is `Format`, which is a *selection
from a device-supplied list*, not a literal value. An `Action<TOptions>`
shape would have to encode selection strategy as builder calls evaluated
later against a snapshot, which is exactly what the fluent builder already
does. Adding both would be two routes to the same destination.

A standalone `OpenAsync(DeviceInfo, Func<CameraSnapshot, CameraConfiguration>, …)`
overload is also **not** added. The factory-delegate idea is correct in
spirit (Decision 4a captures it), but as a top-level `OpenAsync` parameter
it competes with the existing `(DeviceInfo, CameraConfiguration, …)`
overload, divorces the delegate from `CameraSessionOptions` /
`CameraOpenOptions` configuration, and produces error context far from the
diagnostic surface (failures inside the user's delegate). Folding the
delegate into the builder via `UseFormat` keeps the public construction
surface to two doors — records and builder — with the builder owning all
delegate-shaped configuration.

### Decision 5 — System.Reactive interop is opt-in and never primary

`IAsyncEnumerable<LeasedCameraFrame>` remains the primary capture surface
(ADR-0035 §"Golden core shape"). System.Reactive interop, if added, lives in
`Periphery.Camera.Reactive` and is opt-in.

The interop must promote leases to owned frames at the conversion boundary —
`IObservable<LeasedCameraFrame>` is rejected as a public surface because
`IObservable<T>` cannot honor lease disposal contracts across multiple
subscribers, replay buffers, or time-windowed operators. The supported shape
is `IObservable<OwnedCameraFrame>` with documented copy-at-boundary semantics.

This decision is consistent with ADR-0036 §"Backpressure" — Rx is
push-based and has no native backpressure model, so ADR-0036's bounded
queues with named overflow policies remain the authoritative model and Rx is
strictly an off-ramp for callers who want time-based operators.

### Decision 6 — Implementation order is non-negotiable

The layers depend on each other for ergonomic payoff:

1. **Format selectors** — closes the largest piece of `ChooseFormat`
   boilerplate; unlocks the other layers' simplifications.
2. **Frame sinks** — closes the file-buffer use case in the existing
   ADR-0035 frame ownership model. Selectors are not a prerequisite but
   sinks become noticeably less useful in examples without them.
3. **Fluent builder** — purely additive; can ship any time after (1).
4. **`Periphery.Camera.Pipelines`** (ADR-0036 work). Implemented only
   after the core feels ergonomic on its own. The pipelines package must
   not be a workaround for an awkward core surface.
5. **`Periphery.Camera.Reactive`** — last, smallest, opt-in.

## Non-goals

The following are deliberately **not** covered by this ADR. Each remains
governed by an existing ADR or a future one:

- Pipeline graph runtime, branching, metadata propagation, backpressure
  policies — owned by ADR-0036.
- Encoding to compressed video, muxing, network transport — owned by
  `Periphery.Camera.Pipelines` and/or `Periphery.Camera.Encoding`.
- UI preview controls, GPU effects, multi-camera synchronization,
  application-level supervision policy — non-goals already named in
  ADR-0035.

## Consequences

### Positive

- The simple capture-and-save workflow shrinks to a small handful of lines
  with no helpers in user code.
- Selectors and sinks compose with raw LINQ and `IAsyncEnumerable`
  operators — they introduce no new abstraction.
- The boundary between core ergonomics and pipelines becomes testable: a
  sink interprets pixels or it doesn't.
- The fluent-pipelines roadmap (ADR-0036) is freed from carrying ergonomic
  burden that doesn't actually need a graph runtime.

### Negative

- The core package surface grows. The growth is small (≈10 selector methods,
  3 sink methods, an options record, an enum), but it is real surface that
  must be supported across backends.
- The "no encoding in core" rule is a load-bearing constraint that future
  work must respect. Any future contributor adding a sink must pass the
  pixel-interpretation test before landing it in core.

### Neutral / follow-up questions

- Whether `CameraFrameWriteOptions` should support per-frame filename
  delegates (e.g., `Func<ICameraFrame, int, string>`) or whether the
  built-in naming modes are sufficient.
- Whether `ToOwnedAsync` should support a configurable maximum outstanding
  owned-frame count (a back-pressure-on-promotion knob) — defer until a
  consumer scenario warrants it.
- Whether a small subset of pipeline operators (notably `Take`,
  `Throttle`, `Sample`) deserves to live in core for use without the full
  pipelines package. Defer to ADR-0036 implementation work.
- Whether the builder's `UseFormat` escape hatch should also allow the
  caller to express *fallback chains* directly (e.g., a list of
  `Func<CameraSnapshot, CameraFormat?>` evaluated until one returns
  non-null) or whether composing fallbacks via the format selectors is
  sufficient.
