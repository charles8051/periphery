---
title: "ADR-0045: Periphery becomes substrate-independent — ICameraFrame drops Crossbar inheritance"
status: "Accepted"
date: "2026-05-22"
authors: "@charles8051 (decision)"
tags: ["architecture", "decision", "camera", "substrate", "independence", "crossbar", "frameflow"]
supersedes: "0042"
superseded_by: ""
---

# ADR-0045: Periphery becomes substrate-independent

> **Note, added at the source-available release (2026-08-30).** The external
> graph substrate this record drops is, as of that date, a private repository
> a public reader cannot follow. That is a status now, not a reason then — the
> decision below was taken because the substrate forked, not because it was
> abandoned. This is the ADR that records why
> `ICameraFrame` inherits nothing and carries no third-party contract
> types; [ADR-0042](0042-periphery-crossbar-substrate-integration.md) is the
> superseded integration it reversed.

## Status

Supersedes [ADR-0042][adr42] (the prior decision to
have `ICameraFrame` extend `Crossbar.IFrame` and
`Crossbar.IRefCounted`).

Two companion records live in separate, private repositories and are named
here for provenance rather than linked: Crossbar's ADR-0024 (the Crossbar-side
fork charter) and FrameFlow's ADR-0049 (the FrameFlow-side fork ADR, which
absorbs `Periphery.Camera.Pipelines`' content as `FrameFlow.Camera`). Neither
is needed to read this decision — Periphery's side of it is self-contained,
which is the point of the decision.

[adr42]: ./0042-periphery-crossbar-substrate-integration.md
[adr36]: ./0036-periphery-camera-pipelines.md
[adr41]: ./0041-periphery-camera-crossbar.md

## Context

ADR-0042 (2026-05-18) landed Periphery's direct integration with the
Crossbar substrate: `ICameraFrame` extends `Crossbar.IFrame`
(structural frame shape) and `Crossbar.IRefCounted` (substrate item
contract). This let camera frames flow through Crossbar's
`SourceNode<T>` / `SinkNode<T>` / `OperatorNode<TIn, TOut>` directly
— no wrapper layer needed. ADR-0042 explicitly framed this as the
"clean" outcome of a multi-step migration starting in ADR-0036 /
ADR-0041.

The premise of ADR-0042 was a **single substrate** that Periphery
and its consumers (FrameFlow) all bound to. That premise no longer
holds.

The 2026-05-21 / 2026-05-22 design conversation settled a charter tension in Crossbar: it has been trying
to be both a Holoscan-class substrate (CUDA primitives, GPU
scheduling, real-time multi-sensor) *and* an ergonomic media
inference graph runtime (FrameFlow's actual need). The resolution
(see Crossbar's own ADR-0024) is a **fork**: Crossbar continues
with the Holoscan-class charter; FrameFlow forks the substrate as
`FrameFlow.Graph` and slims it for media use.

This creates a problem for Periphery as it stands:

- `ICameraFrame : Crossbar.IFrame, Crossbar.IRefCounted` binds the
  frame contract to *one* of the two substrates (Crossbar's
  evolving Holoscan-class shape).
- FrameFlow now uses `FrameFlow.Graph` primitives. Frames from
  Periphery can't flow through `FrameFlow.Graph`'s operators
  directly because they don't implement `FrameFlow.Graph.IFrame` /
  `FrameFlow.Graph.IRefCounted`.
- Periphery doesn't actually *use* Crossbar's substrate primitives
  inside `Periphery.Camera` — it only *references* the two
  contract interfaces. The substrate-mechanics-using code
  (`SourceNode<ICameraFrame>` adapters etc.) lives in the separate
  `Periphery.Camera.Pipelines` sub-package.

Three options were weighed in the 2026-05-22 conversation:

1. **Periphery stays on Crossbar.** `ICameraFrame` keeps its
   `Crossbar.*` inheritance. FrameFlow writes adapter classes that
   wrap `ICameraFrame` in `FrameFlow.Graph.IFrame`-implementing
   wrappers. Crossbar's evolving Holoscan-class substrate continues
   to be a Periphery dependency that Periphery doesn't actually
   use the bulk of.
2. **Periphery follows FrameFlow.** `ICameraFrame` switches its
   inheritance to `FrameFlow.Graph.IFrame, FrameFlow.Graph.IRefCounted`.
   Periphery now depends on FrameFlow.Graph for two contract
   interfaces. "Periphery doesn't need any graphs" stops being
   literally true.
3. **Periphery defines its own minimal contracts.** `ICameraFrame`
   drops both `Crossbar.*` inheritances. The frame shape (`Width`,
   `Height`, `Timestamp`, `PixelFormat`, planes) is declared
   directly on `ICameraFrame`; the refcount mechanics (`AddRef`,
   `Dispose`) are declared directly without an `IRefCounted` base.
   Periphery becomes substrate-independent. FrameFlow.Camera (a
   new FrameFlow sub-package) provides adapter types that implement
   FrameFlow.Graph's contracts around an `ICameraFrame`.

Option 3 makes Periphery a *genuinely standalone hardware
peripherals library*: it produces camera frames and that's it. Its
consumers — whichever substrate they're built against — adapt.

## Decision

### 1. `ICameraFrame` drops both Crossbar inheritances

`ICameraFrame` becomes a self-contained interface that declares
exactly what a camera frame is, without referencing any substrate
type. The properties that previously came from `Crossbar.IFrame`
(`Width`, `Height`, `Timestamp`) are declared directly. The
refcount mechanics (`AddRef`, `Dispose`) are declared directly,
without an `IRefCounted` base.

```csharp
namespace Periphery.Camera;

public interface ICameraFrame : IDisposable
{
    // Previously inherited from Crossbar.IFrame
    int Width { get; }
    int Height { get; }
    TimeSpan Timestamp { get; }

    // Previously inherited from Crossbar.IRefCounted (covariant return preserved)
    ICameraFrame AddRef();

    // Camera-specific surface (unchanged)
    CameraPixelFormat PixelFormat { get; }
    int PlaneCount { get; }
    bool IsContiguous { get; }
    ReadOnlyMemory<byte> ContiguousBuffer { get; }
    CameraPlane GetPlane(int index);
}
```

The semantics are unchanged: refcount = 1 on construction; `AddRef`
increments; `Dispose` decrements; underlying buffer releases when
the count hits zero. The protocol is identical to Crossbar's
`IRefCounted` — there's no behavior change for callers — but the
type system no longer says "this is a Crossbar item."

`LeasedCameraFrame` and `OwnedCameraFrame` implement the slimmed
`ICameraFrame` directly. Their internal refcount machinery
(currently identical to Crossbar's `IRefCounted` CAS-loop pattern)
stays. The `IRefCounted IRefCounted.AddRef() => AddRef();` forwarding
required by [ADR-0042][adr42] §3 is removed because the base
interface is gone.

### 2. Drop the Crossbar dependency from `Periphery.Camera`

`Periphery.Camera.csproj` removes its `PackageReference` to
`Crossbar`. The three files that touched Crossbar
(`ICameraFrame.cs`, `LeasedCameraFrame.cs`, `OwnedCameraFrame.cs`)
get their `using Crossbar;` lines removed and the inheritance
rewrites land per §1.

After this change, **`Periphery.Camera` has no substrate
dependency at all.** It depends on `Periphery` (its sibling core
package) and the .NET BCL. That's it.

### 3. `Periphery.Camera.Pipelines` dissolves

The sub-package's content — `CameraSourceAdapters.cs`,
`CameraFrameSinkAdapters.cs`, `ICameraFrameSink.cs`,
`CameraFrameMemoryDomain.cs`, `CameraFormatInfo.cs`,
`NullCameraFrameSink.cs` — was the bridge between
`Periphery.Camera` and `Crossbar`'s substrate. With the fork:

- Periphery is substrate-independent (this ADR).
- FrameFlow has its own substrate (`FrameFlow.Graph`, per
  `frame-flow` ADR-0049).
- The bridge belongs in `FrameFlow.Camera` (new sub-package in
  FrameFlow), not in Periphery.

Therefore `src/Periphery.Camera.Pipelines/` and its content are
**deleted from Periphery**. The functionally-equivalent code is
recreated in `frame-flow:src/FrameFlow.Camera/`, rewritten against
`FrameFlow.Graph` instead of `Crossbar`. See `frame-flow` ADR-0049
§4 for the FrameFlow-side recipe.

`Periphery.Camera.Pipelines.csproj` is removed from the solution.

### 4. `Periphery.Camera.Avalonia` stays put

The Avalonia integration package contains UI helpers for camera
preview. Whether the integration's pipeline-shaped pieces (the
`Crossbar`-aware preview adapter, if any) move to FrameFlow is the
same question that applies to `Periphery.Camera.Pipelines` — but
the *UI-shaped* pieces (Avalonia control rendering, dispatcher
glue) stay in Periphery because they're not substrate-flavored.

If `Periphery.Camera.Avalonia` currently references Crossbar at
all, it loses that reference (Periphery is now substrate-free).
If it has any pipeline-shaped content that depended on Crossbar
primitives, that content moves to `frame-flow:FrameFlow.Camera.Avalonia`
(or folds into `FrameFlow.Avalonia` if the cross-cutting
considerations warrant).

### 5. `Periphery.Hid`, `Periphery.Cli`, `Periphery` (core) stay substrate-independent

These sub-packages don't currently use the substrate (HID is
hardware-input shaped; CLI is a developer tool; the `Periphery`
core has cross-camera-and-HID infrastructure). They confirm
substrate-independence as the standing posture: none of them takes
on a Crossbar or FrameFlow.Graph dependency going forward. Any
graph integration for HID (or future microphone, gamepad, etc.)
follows the same pattern this ADR establishes — bridge code in the
*consumer* substrate's sub-package, not in Periphery.

### 6. Periphery's identity, going forward

**Periphery is a standalone hardware peripherals library.** Its
constituents — cameras, HID, future hardware — produce
typed item streams (frames, input events, etc.) with refcounted
ownership semantics. Periphery does *not* depend on any graph
substrate; consumers that want substrate integration provide their
own adapter layer.

The "no consumers yet" stance continues to apply: this ADR's changes are unconstrained by external migration
cost.

## Consequences

### What this enables

- **Periphery is portable to any substrate.** A future graph
  runtime — Crossbar's evolving Holoscan-class shape,
  FrameFlow.Graph, some third runtime that doesn't exist yet —
  can consume `Periphery.Camera` by writing a small adapter layer.
  Periphery doesn't have to follow any substrate's evolution.
- **The dependency direction simplifies.** Today's confusion
  ("Periphery depends on Crossbar; FrameFlow depends on both;
  what does the substrate fork mean for the chain?") goes away.
  `Periphery → (nothing substrate-shaped)`. FrameFlow consumes
  Periphery as a normal upstream dependency.
- **The kiosk consumer (the only external Periphery consumer) doesn't have to think
  about substrate at all.** It uses Periphery's camera frames
  however it likes; if the kiosk consumer ever wants substrate
  integration, it picks one (FrameFlow.Graph is the likely choice
  given the project's focus).
- **Periphery's refcount discipline gets to be its own thing.**
  The current discipline (CAS-loop, throws on AddRef-after-zero,
  pool returns on dispose-to-zero) was deliberately identical to
  Crossbar's `IRefCounted` to make the inheritance work. With the
  base gone, the discipline is documented directly on
  `ICameraFrame` and can evolve to suit camera-specific needs
  (e.g., a `TryAddRef` non-throwing variant, or a `RefCount`
  diagnostic accessor) without coordinating with Crossbar.

### What this rules out (until reopened)

- **Direct substrate-typed flow through Periphery code.**
  `Periphery.Camera` operators (if any are ever needed inside
  Periphery itself) cannot use `FrameFlow.Graph.SourceNode<T>` or
  `Crossbar.SourceNode<T>` directly. Substrate code lives in
  consumer packages.
- **Crossbar / FrameFlow.Graph operators consuming `ICameraFrame`
  without an adapter.** A `Crossbar.OperatorNode<ICameraFrame, T>`
  doesn't compile because `ICameraFrame` no longer implements
  `Crossbar.IRefCounted`. Consumers must adapt first
  (`FrameFlow.Camera`'s `CameraFrameAdapter` is the FrameFlow
  example; a hypothetical `Crossbar.Camera` would write its own).

### Consequences for prior Periphery ADRs

- **[ADR-0042][adr42]** (this ADR's predecessor) is superseded.
  ADR-0042's framing of "Periphery integrates the Crossbar
  substrate directly" was correct under the single-substrate
  premise; with the fork that premise is gone, and the integration
  approach reverses.
- **[ADR-0036][adr36] and [ADR-0041][adr41]** were already
  superseded by ADR-0042; this ADR's supersession of ADR-0042
  doesn't revive them. The
  `Periphery.Camera.Pipelines`-as-sink-contract direction those
  ADRs proposed is also dead — the bridge moves to the consumer
  substrate (FrameFlow.Camera) rather than being a Periphery-side
  sub-package at all.
- **No other Periphery ADRs directly depend on the substrate
  integration**; the rest stay in scope.

### Migration

The mechanical work, under that same "no consumers yet" stance:

1. **Update `ICameraFrame.cs`** per §1: remove `Crossbar.IFrame,
   Crossbar.IRefCounted` inheritance; declare `Width` / `Height` /
   `Timestamp` / `AddRef()` directly on the interface.
2. **Update `LeasedCameraFrame.cs` and `OwnedCameraFrame.cs`**:
   remove `IRefCounted IRefCounted.AddRef() => AddRef();` shims;
   remove `using Crossbar;` references.
3. **Update `Periphery.Camera.csproj`**: remove the `Crossbar`
   PackageReference.
4. **Delete `src/Periphery.Camera.Pipelines/`** and its `.csproj`.
   Remove from the solution file.
5. **Check `Periphery.Camera.Avalonia`** for Crossbar references;
   handle per §4.
6. **Check `tests/` projects** for any direct Crossbar usage tied
   to camera frame substrate flow; update or delete.
7. **Update `README.md`** if it describes Periphery as
   "Crossbar-integrated."

The kiosk consumer follows: it adapts to the
slimmed `ICameraFrame` interface, which is a near-no-op since the
properties (`Width`, `Height`, `Timestamp`, etc.) are unchanged.

### API stability

Breaking changes are on the table. The
interface change is technically breaking (`ICameraFrame` no longer
*is-a* `Crossbar.IFrame`); the property surface is unchanged.
Sibling-repo consumers (FrameFlow, future demos) update by switching
to the FrameFlow.Camera adapter layer. The kiosk consumer updates with
the trivial property-renames-not-required change.

## Alternatives considered

### A. Keep `ICameraFrame : Crossbar.IFrame, Crossbar.IRefCounted`

Status quo from ADR-0042. Now problematic because:

- Crossbar's substrate evolves toward Holoscan-class primitives
  Periphery doesn't use. The dependency surface grows for no
  benefit to Periphery.
- FrameFlow can't directly use `Periphery.Camera` frames — it'd
  need adapters anyway (FrameFlow.Graph.IFrame wrapping
  Crossbar.IFrame wrapping ICameraFrame). The substrate
  inheritance buys Periphery nothing in the post-fork world.

Rejected.

### B. Switch to `ICameraFrame : FrameFlow.Graph.IFrame, FrameFlow.Graph.IRefCounted`

The mirror-image alternative: follow FrameFlow instead of
Crossbar.

Considered. Rejected for the same reason as A in reverse: this
binds Periphery to FrameFlow's substrate evolution, which is
arbitrary from Periphery's perspective. Periphery has its own
identity; binding it to either substrate's evolution is a
coordination cost without an upside.

The user's framing in the 2026-05-22 conversation — *"periphery
doesn't need any graphs"* — applies symmetrically against both
Crossbar and FrameFlow.Graph. Substrate independence is the only
posture that honors that.

### C. Two interfaces — `ICameraFrame` (substrate-free) plus `ICrossbarCameraFrame` extending it with Crossbar bases

Considered briefly. Rejected because it creates two parallel type
hierarchies with no real benefit — substrate-binding belongs in
the consumer (FrameFlow.Camera adapter), not in Periphery offering
multiple flavors. Periphery should ship one interface; consumers
adapt.

### D. Move `Periphery.Camera` into FrameFlow

Considered during the 2026-05-22 conversation and stepped back
from. Periphery has a genuine non-camera identity
(`Periphery.Hid`, `Periphery.Cli`, future microphone / gamepad)
that justifies keeping it as a standalone library. Moving the
flagship sub-package (`Periphery.Camera`) into FrameFlow would
hollow out Periphery's identity without commensurate benefit; the
dependency edge from FrameFlow → Periphery.Camera (option chosen)
captures the consumption relationship without the relocation cost.

## References

- [ADR-0042][adr42]: prior decision; superseded by this ADR.
- [ADR-0036][adr36], [ADR-0041][adr41]: ancestors superseded by
  ADR-0042; not revived by this ADR.
- Crossbar ADR-0024: the Crossbar-side fork charter that
  motivates this Periphery-side change.
- FrameFlow ADR-0049: the FrameFlow-side fork ADR that
  absorbs `Periphery.Camera.Pipelines`' content as
  `FrameFlow.Camera`.
- Periphery's "no consumers yet" stance — no external consumers and no
  stability commitment — which is what makes this kind of breaking change
  cheap.
- 2026-05-22 design conversation (in transcripts): the dialogue
  that surfaced the three-option choice and settled on Option C
  (substrate-independence).
