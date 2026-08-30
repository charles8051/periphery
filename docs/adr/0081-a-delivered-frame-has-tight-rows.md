---
title: "ADR-0081: A delivered frame has tight rows"
status: "Accepted"
status_note: "Accepted 2026-08-25 on the three-camera measurement. Implemented in 375e497 (`#332`)."
date: "2026-08-24"
amended: "2026-08-25"
authors: "@charles8051"
tags: ["architecture", "decision", "camera", "frame-layout", "stride", "pixel-format", "media-foundation", "v4l2", "performance", "contract"]
supersedes: ""
superseded_by: ""
depends_on: "0035-periphery-camera.md"
---

# ADR-0081: A delivered frame has tight rows

## Status

Refines [ADR-0035](0035-periphery-camera.md) Decision 11, which required the frame
contract to expose per-plane stride so consumers could work "without mandatory
repacking". This ADR keeps the stride on the record and removes the variation it was
describing. Resolves [#320](https://github.com/charles8051/periphery/issues/320);
unblocks [#316](https://github.com/charles8051/periphery/issues/316),
[#317](https://github.com/charles8051/periphery/issues/317) and
[#318](https://github.com/charles8051/periphery/issues/318).

Accepted 2026-08-25, after the measurement in Context was extended from one camera to
three across two platforms. Implemented in 375e497 (`#332`): the decisions below describe
the contract the pool holds today, asserted on every delivered frame.

**Accepted knowing the packed-format case is weak.** The Open Questions record that no
Media Foundation measurement has observed padding on a packed format, and that four
packed modes across two devices were tested at non-64-aligned strides and all came back
tight. `#320`'s titular defect is unreached on every camera measured.

**And D3 alone would have closed `#320`.** `#320` is that `BuildPlanes` recomputed the
stride instead of carrying the one the backend measured. D3 — the source layout is
described per plane, never inferred from one stride — is the half that fixes it: carry
the measured layout, report it, done. No invariant, no repack, no one-way door. D1 is
an ergonomic decision layered on top of the fix, not part of it, and a future reader may
reverse D1 without disturbing the bug fix. It is justified by what padding *is* — a
platform artifact with no consumer value, carried across a copy the library already
performs — and by a consumer survey in which nobody trusts the stride field. It is not
justified by allocations; that argument appeared in an earlier revision, was false, and
is withdrawn below.

## Context

`CameraPlane.Stride` is wrong today for every packed single-plane format.
`CameraFramePool.BuildPlanes` recomputes the natural stride via
`CameraFrameLayout.BytesPerRow` instead of carrying the one the backend measured
(`src/Periphery.Camera/Internal/CameraFramePool.cs:102`), because
`PlaneLayout.DescribePlanes` returns `null` for every non-4:2:0 format
(`PlaneLayout.cs:104-105`). Meanwhile the buffer really does hold the padding: Media
Foundation copies `absStride * height` bytes (`MfCameraBackend.cs:385-392`) and V4L2
copies `buf.BytesUsed` (`V4l2CameraBackend.cs:1008-1015`). The bytes and the contract
disagree, and a consumer walking rows by `Stride` reads progressively skewed pixels
with no exception.

Commit `b887a9e` fixed the same defect for planar formats. This is the packed half of
one mistake, made twice.

The obvious repair is to carry the measured stride. The alternative nobody had
evaluated is to remove the padding instead. That is the question this ADR settles.

### Padding is real, and it was measured on three cameras

Every advertised uncompressed mode of each camera was *attempted*: opened through the
live backend, capture started, and one frame read, comparing the delivered buffer length
against `CameraFrameLayout.FrameSize`. Modes that delivered a frame are the measurement
population; MJPEG is excluded, having no fixed size to compare against.

**Media Foundation, AVerMedia PW513:**

| Mode | Format | Tight stride | Reported | Verdict |
|---|---|---|---|---|
| 480×360 | NV12 | 480 | 512 | padded |
| 800×448 | NV12 | 800 | 832 | padded |
| 800×600 | NV12 | 800 | 832 | padded |
| 848×480 | NV12 | 848 | 896 | padded |
| 11 other NV12 modes | NV12 | = width | = width | tight |
| all 15 UYVY modes | UYVY | = width×2 | = width×2 | tight |

**Media Foundation, NexiGo N60 FHD (`3443:60BB`)** — a different vendor, probed
2026-08-25:

| Mode | Format | Tight bytes | Actual bytes | Stride | Verdict |
|---|---|---|---|---|---|
| 480×272 | NV12 | 195,840 | 208,896 | 480 → 512 | padded |
| 800×600 | NV12 | 720,000 | 748,800 | 800 → 832 | padded |
| 1440×1080 | NV12 | 2,332,800 | 2,384,640 | 1440 → 1472 | padded |
| 6 other NV12 modes | NV12 | — | = tight | = width | tight |
| all 7 YUY2 modes | YUY2 | — | = tight | = width×2 | tight |

**Media Foundation, Logitech C270 (`046D:0825`)** — 19 YUY2 modes all tight, and **10
of its 19 NV12 modes padded**:

| Mode | Stride | | Mode | Stride |
|---|---|---|---|---|
| 160×120 | 160 → 192 | | 752×416 | 752 → 768 |
| 176×144 | 176 → 192 | | 800×448 | 800 → 832 |
| 352×288 | 352 → 384 | | 800×600 | 800 → 832 |
| 432×240 | 432 → 448 | | 864×480 | 864 → 896 |
| 544×288 | 544 → 576 | | 1184×656 | 1184 → 1216 |

> **Correction.** An earlier revision of this ADR said the C270's NV12 modes never
> delivered a frame, and issue `#327` was filed claiming Media Foundation advertises
> formats the device cannot produce. Both were wrong. That reading came from a first
> probe run in which every C270 NV12 mode timed out; the run logged
> `MF cleanup did not complete within 3s; abandoning` on each failure, and the driver
> was wedged — plausibly by the probe's own teardown. A second run on a settled driver
> delivered all nineteen. The saved output is the second run.

Seventeen padded modes across the three cameras — four on the PW513, three on the N60,
ten on the C270 — not four across two.

The rule is the same on all three: **Media Foundation rounds the NV12 luma stride up to
a 64-byte multiple.** Every padded mode above lands exactly on one — 192 = 3×64,
384 = 6×64, 448 = 7×64, 576 = 9×64, 768 = 12×64, 832 = 13×64, 896 = 14×64, 1216 = 19×64,
1472 = 23×64 — and every width already divisible by 64 comes back tight. It was
predicted before it was measured on the PW513, then held unchanged on two more vendors.

Three independent vendors and seventeen instances is what makes this a platform rule
rather than one card's firmware. Nor is it confined to odd widths: 160, 800, 848 and
1440 are all multiples of 16, and 160 is a multiple of 32.

**Linux does not do this, and that was measured too.** `uvc_v4l2_get_bytesperline()`
returns `wWidth` for the 420 family and `bpp * wWidth / 8` otherwise, with no alignment.
Probing the N60 and the C270 through V4L2 on the Linux device rig confirms it: 26
uncompressed modes, zero padded. Three of those tight strides are *not* 64-aligned —
YUYV 176×144 at 352, 432×240 at 864, 752×416 at 1504 — and uvcvideo reported each
exactly. Under MF's rule they would have become 384, 896 and 1536.

A rule that holds on V4L2 and fails on Media Foundation is the kind that ships green and
breaks in the field.

### A copy already happens on every frame

`CameraFramePool.cs:42` runs `raw.Data.Span.CopyTo(buffer)` unconditionally, on both
backends, and again at `:61` for the overflow path. V4L2 is zero-copy up to that line.
Media Foundation copies twice — once into a reused managed `_frameBuffer`
(`MfCameraBackend.cs:392`), then again into the pool.

De-padding therefore adds no pass. It changes the inner loop of a copy that is already
mandatory. The usual objection to de-padding is that it inserts a copy into a zero-copy
path. There is no zero-copy path here.

### Cost, measured

Windows 11, .NET 10, Release, DRAM-cold against a ~400 MiB working set:

| Case | Bulk copy | Row loop, de-padding | Delta | % of 30 fps budget |
|---|---|---|---|---|
| 1080p BGRA32 — *hypothetical* | 0.455 ms | 0.755 ms | +0.300 ms | 0.90 % |
| 4K BGRA32 — *hypothetical* | 1.564 ms | 3.024 ms | +1.460 ms | 4.4 % |
| **848×480 NV12** — *observed* | 0.058 ms | **0.052 ms** | **−0.006 ms** | −0.02 % |

**Only the last row describes a case that occurs.** No packed format padded on any of
the three cameras, so the BGRA figures measure a de-pad that never runs. Every mode that
actually pads is NV12, where the de-padded copy moves fewer bytes and measured faster.
The real cost on measured hardware is zero or slightly negative, and the BGRA rows are
kept only to bound what it would cost if a packed format ever did pad.

The cases that actually pad are free or cheaper, because de-padding at 848×480 NV12
moves 34,560 fewer bytes than it copies. Padding only appears when the width is not
64-aligned, and the large standard resolutions all are. Tight frames keep the bulk
path unchanged.

The de-pad delta at 1080p is smaller than the second Media Foundation copy that
already happens and that nobody has objected to.

### What the prior art actually says

The received framing — low-level APIs expose stride, convenience APIs hide it — does
not survive the survey. The real split is **mapping versus copying**.

| API | Exposes stride | De-pads |
|---|---|---|
| MF `IMF2DBuffer2::Lock2DSize` | yes, signed pitch | no |
| MF `IMFMediaBuffer::Lock` | no | yes |
| MF `IMF2DBuffer::ContiguousCopyTo` | n/a | yes, explicitly |
| V4L2 `bytesperline` | yes | no |
| AVFoundation `CVPixelBufferGetBytesPerRow` | yes | no |
| Android `Image.Plane.getRowStride` | yes | no |
| GStreamer `GstVideoMeta` | yes | no |
| FFmpeg `AVFrame.linesize[]` | yes | no |
| WinRT `BitmapPlaneDescription.Stride` | yes | no |
| `System.Drawing.BitmapData.Stride` | yes | no |
| **OpenCV `VideoCapture::retrieve()`** | **no** | **yes, by copy** |

Every API that hands out a pointer into memory it did not allocate exposes stride,
because it has no choice. It is describing a buffer it does not own. Those are views,
and a view cannot de-pad without becoming a copy.

The one API in the table that de-pads is the one that copies. OpenCV's MSMF backend
calls `Lock2D`, wraps a strided `Mat` header over the padded surface, then `copyTo`s
into a `Mat::create` destination that is continuous by construction. `Mat::isContinuous()`
is exactly that contract.

FFmpeg is not a counterexample. It exposes padding because it *adds* padding, on
purpose, for SIMD row alignment. Periphery has no kernels asking for aligned row
starts.

Periphery is a copying API. Every frame is memcpy'd into a pooled managed `byte[]`,
refcounted and recycled rather than mapped, and `ICameraFrame` is a managed record
rather than a lock scope. On the mapping-versus-copying axis it sits with OpenCV's
`VideoCapture`, not with `Lock2D`.

### `IsContiguous` currently means something else

`ICameraFrame.IsContiguous` is implemented as `_planes.Length <= 1`
(`LeasedCameraFrame.cs:53`). It means "one plane", not "no padding". Media Foundation,
whose vocabulary the name borrows, defines a contiguous representation as one "with no
additional padding" and ships `ContiguousCopyTo` to produce it. The property name is
currently a false friend.

### No consumer needs the padding

Surveyed across `frame-flow` and the kiosk consumer. There is not one stride-required site.

`FrameFlow.Camera.CameraVideoFrame.BuildView` trusts `plane0.Stride` and warns in a
comment about swscale "reading sideways through the buffer". That comment is about
*truthfulness*, not padding. swscale accepts any stride at or above the row width, and
`SwScaleVideoConverter` already emits tight output deliberately.

Two of the kiosk consumer's subsystems de-pad by hand today because their own downstream demands
tight rows — `CameraRoleIdentificationProbe.Pack` and `ChamberFrameGrabber.PackBgra`.
`FrameChromaStats.AllRatiosFromBgra` takes a span with no width and no stride, so a
padded buffer would average padding bytes into a chroma ratio and silently mis-identify
a camera role.

Four further call sites infer the stride as `Length / Height` rather than reading
`GetPlane(0).Stride`. Nobody reads the field. That is the shape of a library whose
consumers do not believe its stride, and it is evidence the field is not carrying its
weight as a contract.

### What de-padding is actually for

Not allocations. An earlier revision of this ADR made a per-frame allocation cost its
primary argument: the pool seeds at the tight `FrameSize`, so a padded frame misses its
buffer and allocates a replacement. That is true of the *first* frames only.
`CameraFramePool.Return` enqueues whatever buffer it is handed, including the oversized
replacement, so after `BufferCount` frames every pooled buffer is the padded size and
allocation stops. The cost is roughly 7 MB once per session at NV12 1440×1080, not 2.3 MB
per frame. The argument was wrong and is withdrawn.

The real case is narrower.

**Padding is a platform artifact with no consumer value.** It exists because a driver
aligned rows for its own convenience. It says nothing about the image. The measurement
shows exactly how arbitrary it is: Media Foundation pads NV12 on all three cameras and
V4L2 pads nothing on the same two devices. Preserving it means every downstream contract
inherits a case that varies by driver, by resolution and by operating system, in exchange
for information nobody wants.

**Periphery is a copying API, and the copy is structural rather than incidental.** V4L2
must return the mmap'd buffer to the driver via `VIDIOC_QBUF`; Media Foundation must
unlock the surface. Neither backend can hand out the platform's memory, so a copy into a
library-owned buffer is unavoidable. Given a copy you already perform, into a buffer you
allocated yourself, carrying the source's alignment forward is preserving a wart for
nobody. This is the mapping-versus-copying argument in the prior-art section, and it is
what the decision rests on.

**The consumers say the same thing.** Two of the kiosk consumer's subsystems de-pad by hand today
because their own downstream demands tight rows. `FrameChromaStats.AllRatiosFromBgra`
takes a span with no width and no stride, so a padded buffer would average padding bytes
into a chroma ratio and silently mis-identify a camera role. Four further call sites
infer stride as `Length / Height` rather than reading the field. That is a library whose
consumers do not believe its stride, and it is the strongest evidence in this document.

## Decision

**D1. A delivered frame has tight rows, in the CPU memory domain.** No plane of an
uncompressed frame the pool delivers carries inter-row padding. This is an invariant,
asserted in the pool, not a convention.

**Scoped deliberately.** The claim is about frames this pool delivers, which are
`CameraFrameMemoryDomain.Cpu` and copied into library-owned managed buffers. It is not a
claim about every frame Periphery might ever produce. A GPU-resident frame would not come
through this pool at all — it would be discriminated by `MemoryDomain`, carry whatever
stride its surface has, and leave this invariant untouched. `CameraFrameMemoryDomain`
exists today with exactly one member for that reason.

That scoping is what answers the one-way-door objection in the Open Questions. The door
is only one-way if the invariant is global.

Stated as three properties, all checkable:

1. Plane 0's stride is the natural row width, `CameraFrameLayout.BytesPerRow(format, width)`.
2. Each plane's rows exactly fill its extent: `Stride * Height == Length`.
3. The planes tile the frame with no gaps: plane *n* begins where plane *n-1* ends, and
   the total equals `CameraFrameLayout.FrameSize(format, width, height)`.

An earlier draft wrote this as the single formula
`CameraPlane.Stride == BytesPerRow(format, planeWidth)` for every plane. **That formula
is wrong for interleaved chroma.** An NV12 chroma plane reports `Width = width / 2`,
because it carries half as many samples per row, while its real row is `width` bytes —
`width / 2` two-byte interleaved UV pairs. Feeding the plane's `Width` back through
`BytesPerRow` yields `width / 2` and the assertion fails on a correct frame. The three
properties above say what was meant without depending on a per-plane `Width` that counts
samples rather than bytes.

Found while implementing `#320`. The discrepancy is documented on `CameraPlane` as well,
since a consumer reading `Width` and `Stride` together will hit the same surprise.

**D2. The de-pad happens in the pool's existing copy.** `CameraFramePool.TryDeliver`
and `ForceDeliver` replace the bulk `CopyTo` with a plane-aware copy. One place, both
backends, no per-backend duplication. Per the functional-core preference, the layout
computation is a total function of the source plane descriptors and the target format;
only the copy touches memory.

The bulk `CopyTo` survives as a fast path, but its precondition is **layout equality,
not tight strides**. Tight rows alone are not sufficient, in two ways that both produce
a corrupt frame rather than an error:

- A producer can emit tight per-plane strides while seating a later plane at an offset
  beyond the end of the previous one. An NV12 source with `UV.Offset > Y.Height *
  Y.Stride` has an inter-plane gap, and a bulk copy transplants that gap into a
  destination laid out without one, displacing every chroma row.
- A bottom-up Media Foundation frame can have `|pitch|` exactly equal to the tight row
  width. Tightness says nothing about direction, and a bulk copy preserves bottom-up
  order where the target layout is top-down.

So the fast path requires that for every plane the source `Offset`, `Stride`, `Width`
and `Height` equal the target layout's, *and* that the source is top-down. Anything
else goes down the per-plane row loop, which is also the path that performs the flip
required by D8. Expressed as one predicate: take the bulk copy only when the source
descriptor set is byte-for-byte the layout `PlaneLayout.DescribePlanes` would produce
for the target. That is a pure comparison of two descriptor sets, and it is the
condition being asserted anyway under D1.

**D3. The source layout is described per plane, never inferred from one stride.**
`RawCameraFrame.Planes` is populated for *every* uncompressed frame, including
single-plane ones, where today it is `null` for everything that is not 4:2:0. Each
`RawPlaneDescriptor` already carries `Offset`, `Length`, `Stride`, `Width` and
`Height`, which is exactly what the copy needs.

The pool must not derive chroma addresses from a luma stride. A source whose planes
have unequal strides — V4L2's multi-planar API exposes a `bytesperline` per plane, and
a hardware path could produce one — would otherwise have its chroma read at the wrong
offsets while the *output* stride still looked tight, which is a corruption no
assertion on the result would catch. Periphery's V4L2 backend is single-planar today
(`V4l2CameraBackend.cs:1008` reads the one `v4l2_pix_format.BytesPerLine`), so the
hazard is latent rather than live. The contract should not depend on that staying true.

Backends keep reporting the true platform stride inward. It is internal and never
reaches a consumer.

**D4. `Stride` stays on the public record.** It is redundant under D1 and it stays
anyway, because a stated number is checkable and a derived one is an assumption every
call site re-derives. Four consumers currently compute `Length / Height` rather than
read it; the fix is a field worth reading, not no field.

**D5. `IsContiguous` means the buffer can be read as one run of bytes.** For an
uncompressed frame that is single plane *and* tight rows. For MJPEG it is
unconditionally `true`: a compressed frame is one opaque run with no rows to pad, which
is precisely the case a consumer asks `IsContiguous` about before handing
`ContiguousBuffer` to a decoder.

Stated as one rule: `IsContiguous` is true when `ContiguousBuffer` can be read as a
single run of uniform geometry. That is one plane whose rows are tight, or a compressed
blob.

**Note what this is not.** It is not "are the bytes adjacent". Under D1 the planes tile
the frame with no gaps, so a tight NV12 frame *is* one adjacent run of bytes and
`IsContiguous` is still false for it. The reason is geometry, not adjacency: the luma
and chroma planes have different sample dimensions, so there is no single stride that
describes the buffer, and a consumer handed it as one image reads chroma as if it were
luma. An earlier draft of this decision said "false only when the bytes cannot be walked
linearly", which is wrong in exactly this case and would justify returning true for
NV12. Do not implement to that sentence.

Under D1 and D7 no delivered frame is ever false for the padding reason, so in practice
this reduces to plane count and the value is unchanged from today. The definition is
what changes, and it changes so that the name stops being a false friend.

**D6. There is no opt-out flag.** A flag makes `Stride` conditionally meaningful, which
is the state that produced `#320`. Every consumer would still have to handle both cases
to be correct against an option it does not control, so no consumer's code gets
simpler. It doubles the test matrix across thirteen pixel formats while the padded
branch stays rarely exercised, which is where the next stride bug would live. Adding
the flag later is cheap and non-breaking, so there is no option value in adding it now.

**D7. MJPEG is exempt.** Compressed, variable length, no rows. `Stride` is not
meaningful for it and D1 does not apply.

**D8. Media Foundation's negative stride is normalised before D1 applies.** The flip
and the de-pad are one pass.

Before this ADR, `MfCameraBackend` flipped bottom-up frames itself in a row loop that
copied exactly `height` rows — correct for a packed format and wrong for 4:2:0, which
needs `height * 3/2`, leaving stale chroma in the back half of the buffer. It had not
fired because MF reports negative stride mainly for RGB.

As implemented in `375e497` the backend no longer flips at all: it copies the surface in
storage order and reports `BottomUp`, and the pool's row loop performs the flip across
every plane. That removes the latent bug by construction rather than patching the row
count, and keeps all layout normalisation in one place.

### How this refines ADR-0035 Decision 11

D11 required the frame contract to expose "per-plane stride and extents" so consumers
could work "without mandatory repacking", and forbade flattening formats "into an
ambiguous blob that forces every downstream consumer to reverse-engineer layout
details."

De-padding is repacking, and it is mandatory. That is a real tension and it is why this
is an ADR rather than a bug fix.

The reading that survives: D11's goal was that a consumer never has to reverse-engineer
layout. A stride that varies by driver, resolution and platform is exactly the thing
being reverse-engineered — four consumers guess it from `Length / Height` today. A
stated invariant serves D11's goal better than a variable field does. And the repack is
not additive; the copy D11 was trying to avoid already happens on every frame, twice on
Windows.

What D11 got right stands: plane count, per-plane extents, and explicit layout for
NV12 / I420 / YUY2 remain first-class. Only the claim that the stride must vary to
avoid a copy is refined, because there is no copy to avoid.

## Compatibility

**This is a breaking change to a shipped contract, and it ships as one.** A consumer
on Periphery 3.1.0 that reads `CameraPlane.Stride` or sizes anything from
`ContiguousBuffer.Length` gets different numbers afterward, with no type change and no
compiler error to warn them.

The repo stance is that Periphery has no external consumers, is not
published publicly, and is not committed to API stability. Breaking
changes are the expected shape of progress here, and compatibility shims are
explicitly ruled out. So the answer is not a versioned or opt-in contract — see D6 for
why an opt-out makes the field worse rather than safer. The answer is that this lands
in a major version and the sibling repos follow.

Concretely:

- Ship behind a major bump. The Periphery family co-versions, so every package moves
  together.
- The release note must say "frames are now delivered with tight rows; `Stride` is
  always `BytesPerRow`" in those words. A consumer that never reads `Stride` is
  unaffected, and per the survey above that is all of them today.
- The kiosk consumer and frame-flow update in the same pass. Both are already tight-safe; the
  work is deleting the hand-rolled de-pads, not repairing breakage.
- If this stance ever ends — a first external consumer, a published version with a
  stability commitment — the decision to revisit is D6, not D1. Adding the opt-out
  later is non-breaking; removing the invariant is not.

## Consequences

- `#320`'s four-line stride fix is replaced by a plane-aware copy in one place. The
  recompute-vs-carry pattern that produced the bug twice stops being reachable.
- `#317`'s OpenCV mapping table becomes correct as written. A padded plane cannot be
  wrapped as a single `(h*3/2) × w CV_8UC1` Mat; a tight one can. The unresolved
  disagreement over whether OpenCV's three-plane `cvtColor` honours a padded I420
  stride becomes moot.
- `#316`'s `CameraFramePin` hands out a pointer to a buffer whose stride is known at
  compile time from format and width.
- `#318`'s `WriteableBitmap` row copy gets a known-tight source.
- The pool's reallocation branch becomes unreachable for uncompressed formats, because
  the seed size becomes exact.
- The kiosk consumer can delete the hand-rolled de-pad in `CameraRoleIdentificationProbe.Pack`
  and `ChamberFrameGrabber.PackBgra`.
- 4K BGRA at 60 fps would spend 8.7% of the frame budget on the de-pad. No such
  consumer exists, and 3840 is 64-aligned so this device would not pad it anyway.
- Testing the invariant needs the `InMemoryCameraBackend` stride hook from
  [#321](https://github.com/charles8051/periphery/issues/321), which is a prerequisite
  either way.

## Open questions

**~~The measurement is one camera.~~ Closed 2026-08-25.** The probe was re-run on a
NexiGo N60 FHD and a Logitech C270, on Media Foundation and again on V4L2 through the
Linux device rig. The 64-byte NV12 rule reproduced unchanged on a second vendor,
and Linux showed zero padding across 26 uncompressed modes including three non-64-aligned
strides. Three cameras, two platforms, one rule.

A RealSense or an integrated laptop sensor would still be worth probing if one turns up,
but the platform rule itself is no longer the uncertain part. What the extra measurement
did instead was sharpen the objection in the next paragraph considerably.

**No Media Foundation measurement has observed padding on a packed format, and the
case was tested directly rather than merely unobserved.** This is the strongest
objection to the ADR and the measurement made it stronger, not weaker.

Across all three cameras every padded mode is NV12, where `PlaneLayout.DescribePlanes`
already threads `lumaStride` correctly — the probe watched `GetPlane(0).Stride` report
512, 832 and 1472 on exactly those rows. So `#320`'s actual defect, the packed-format
branch, has zero observed instances.

Worse for the correctness argument, the hypothesis was tested and did not hold. Four
packed modes across two devices have a tight stride that is **not** 64-aligned — exactly
the case where MF's NV12 rule would predict padding — and every one came back tight:

| Device | Mode | Tight stride | 64-aligned? | MF reported |
|---|---|---|---|---|
| Logitech C270 | YUY2 176×144 | 352 | no | 352 |
| Logitech C270 | YUY2 432×240 | 864 | no | 864 |
| Logitech C270 | YUY2 752×416 | 1504 | no | 1504 |
| AVerMedia PW513 | UYVY 848×480 | 1696 | no | 1696 |

An earlier draft argued that MF's 64-byte rule is a property of the platform's buffer
allocation rather than of the pixel format, so a packed format at a non-aligned stride
ought to pad as well. **That argument does not survive the table above.**

Scope it honestly: this is four modes on two devices, in YUY2 and UYVY. It establishes
that those modes deliver tight on that hardware, not that Media Foundation never pads a
packed format on any device or in any format. A third device, or BGRA32 which neither
camera offers, could still pad. What can be said is that the one prediction the rule
made about packed formats was checked and came back negative every time.

So the correctness case for de-padding packed formats is weak on current evidence, and
the ADR should not lean on it. What the decision actually rests on:

1. **Padding is a platform artifact with no consumer value**, carried across a copy the
   library must perform anyway — see "What de-padding is actually for". This replaces
   the allocation-per-frame argument an earlier revision made reason 1; that argument
   was false and is withdrawn.
2. **NV12 padding is real** and reproduces across three vendors.
3. **The consumers do not trust the stride field.** Two of the kiosk consumer's subsystems de-pad by
   hand, `FrameChromaStats` would silently mis-identify a camera role on padded input,
   and four call sites infer stride from `Length / Height`.
4. **One invariant is cheaper to hold than a conditional field**, per D6.

None of those depend on the packed branch ever firing. A reader who concludes that
`#320`'s titular bug is unreached on the hardware measured so far is right, and D1 still
stands on the three reasons above.

**It is a one-way door on an invariant.** Once `Stride == BytesPerRow` is documented,
consumers will stop reading `Stride`. Reintroducing padding later for a D3D11 or
GPU-mapped backend would then be a silent breakage rather than a compile error.
`D3D11_MAPPED_SUBRESOURCE.RowPitch` is documented to be "larger than anticipated
because there might be padding between rows", so that backend is where this would hurt.

**What would reverse this.** A GPU-mapped or D3D11 capture path, or a decision that
`CameraFramePin` should pin the platform buffer rather than the pooled one. Either
makes Periphery a mapping API and inverts the recommendation. A charter decision that
Periphery mirrors platform semantics on principle, independent of consumer need, would
also settle it the other way; that is a defensible position and not a factual
disagreement.

### The GPU reversal condition has a measured price

A GPU-resident video path has already been built twice, in `frame-flow`, and that
record should set the bar for exercising the reversal above curiosity. That work
was presentation-side (D3D11VA decode to compositor) rather than capture-side, so it
does not transfer directly. The operational failure modes do.

Every claim below is sourced from the private `frame-flow` repository, pinned to the
commit that last touched each document so a future reader is checking the text this
section was written against rather than whatever it says later:

| Claim | Source, at commit |
|---|---|
| Interop design | `docs/adr/ADR-0016-avalonia-presenter-frame-delivery-strategy.md` @ `7e9cf0c`; `docs/adr/ADR-0038-memory-domain-pipeline-operators.md` @ `a91da1f` |
| Spike result (240 frames, 0 dropped) | `examples/FrameFlow.Examples.ZeroCopyInterop/README.md` @ `677b33a` |
| CPU table, overlay revert, un-killable processes, Phidget cascade | `docs/adr/ADR-0061-dcomp-overlay-video-surface.md` @ `8bcbd0a`, Post-mortem section |
| Overlay benchmark methodology | `docs/investigations/2026-06-06-dcomp-mpo-overlay-video-presenter.md` @ `af24fb4` |
| Teardown deadlock, 854 MB heap dump, §9 `VideoProcessorBlt` hang | `docs/investigations/2026-06-12-composition-interop-presenter-teardown-deadlock.md` @ `de7710e` |
| `VideoProcessorBlt` replaced by a pixel shader | `docs/adr/ADR-0063-nv12-pixel-shader-color-conversion.md` @ `810bcb1` |
| Containment files | `src/FrameFlow.Avalonia.Windows/` |
| Reverted overlay code | branch `archive/dcomp-overlay` @ `18a3434`; tag `v0.4.1-alpha.1` @ `9f27cd5` |

**What the pins do and do not guarantee.** A commit id fixes *content*: if the object
is reachable, its bytes are the bytes this section was written against. It guarantees
nothing about *availability*. A force-push, a garbage collection, or losing access to
`charles8051/frame-flow` makes these references dead, and this ADR accepts that
dependency knowingly.

So the load-bearing evidence is reproduced here rather than linked: the CPU table above,
and the three excerpts in the appendix. The pins remain for anything a reader wants to
read in full. Whole documents are not mirrored, because a mirror drifts as the original
changes — ADR-0061 was reverted once and ADR-0016 amended twice — while a dated excerpt
under a commit pin is self-evidently a snapshot and cannot pretend to be current.

**Zero-copy compositor interop** (spiked 2026-06-04) worked, and shipped as
`src/FrameFlow.Avalonia.Windows`. Measured on a deployed box
(dual-core Intel with integrated graphics, 1080p), total process CPU as a percentage of one
core, attract clip only with non-video subsystems mocked off:

| Path | Total CPU | GPU 3D | GPU VideoProcessing |
|---|---|---|---|
| HW decode + CPU present (readback) | ~164% | ~33% | 0 |
| HW decode + zero-copy interop | ~73% | ~23% | ~11% |
| HW decode + DComp overlay | ~82% | ~19% | ~11% |

Interop roughly halved CPU. It is opt-in behind `--presenter gpu`, and CPU is still the
default.

**The DirectComposition/MPO overlay** was accepted, shipped in v0.4.0, and reverted the
next day, 2026-06-07. It was a wash on CPU against interop, as the table shows, and the
earlier figure that motivated it measured the presentation component in isolation rather
than total process cost. The decisive failure was resilience: every overlay process wedged un-killable on `taskkill` while every CPU and
interop process died cleanly. The zombie then held the USB stack, the next kiosk
instance could not claim its Phidget hub, and that was the root of a field incident.

**The surviving path was not free either.** A remote-desktop session coinciding with a
view teardown hard-hung the Avalonia UI thread inside a native D3D11 COM `Release`,
producing an unkillable process that needed a reboot and an 854 MB heap dump to
diagnose. Its §9 addendum found a third failure mode on the fix deploy, a
`VideoProcessorBlt` hang under concurrent streams, resolved by dropping
`VideoProcessorBlt` for an HLSL pixel shader. `FrameFlow.Avalonia.Windows`
carries `D3D11DeviceLoss`, `PresenterStallEvaluator`, `PresenterStallWatchdog`,
`PresenterTeardownReaper` and `PresentPlanner` — five files that exist only to contain
GPU failure modes.

Periphery has never spiked any of this. ADR-0036 and ADR-0041 each reserved a
memory-domain seam and both are superseded; `CameraFrameMemoryDomain` still has one
member.

**This narrows nothing.** Both triggers above stand unchanged: a GPU-mapped or D3D11
capture path reopens D1, and so does a decision to pin the platform buffer. A backend
can create that need on its own — if a platform only offers a format or a frame rate
through a DXGI path, the memory model is forced and no consumer has to ask first. That
is a real reversal, and it is not the one this section is about.

What this section rules out is one thing: **a spike undertaken for no reason other than
to test D1.** That would re-import a class of device-loss and teardown problems the
took two months to contain, to answer a question nothing is currently
blocked on. If GPU residency becomes necessary — from a backend constraint, a consumer
requirement, or a platform that leaves no alternative — reopen this decision on that
need, and read the record above as a cost estimate rather than a discouragement.

---

## Appendix: the frame-flow evidence, quoted

Verbatim excerpts from the sources pinned above, so the reasoning in "The GPU reversal
condition has a measured price" is auditable from this repository alone. Each is a
snapshot of the cited commit, not a mirror of a living document; read the source for
the full record.

**Benchmark methodology** — `ADR-0061-dcomp-overlay-video-surface.md` @ `8bcbd0a`:

> A controlled, isolated re-benchmark (attract clip only, all non-video subsystems
> mocked off, Splashtop disconnected) measured *total* kiosk-process CPU, not just the
> presentation component

**Why the overlay was reverted** — same document, Post-mortem §2:

> The decisive failure was resilience, not performance. […] On *abrupt* termination
> (`taskkill` / `Stop-Process` — i.e. every redeploy and every crash) there is no
> graceful dispose, and the GPU/display-driver cleanup of that app-owned, cross-process
> composition state **deadlocks in kernel mode**, leaving an un-killable 1-thread zombie
> that only a reboot clears. Controlled evidence: across the benchmark matrix, **every**
> DComp-overlay process wedged un-killable on kill while **every** CPU/interop process
> (which differ only in the presenter) died cleanly.

**The teardown deadlock on the surviving path** —
`2026-06-12-composition-interop-presenter-teardown-deadlock.md` @ `de7710e`:

> The zero-copy GPU presenter can **hard-hang the Avalonia UI thread** in a native D3D11
> COM `Release` during view teardown. […] The compositor acquires those textures on its
> render thread with an **effectively infinite timeout (~24.8 days)**; when it is wedged
> mid-display-transition (e.g. a remote-desktop connect), the producer's `Release` blocks
> forever on the UI thread → Windows "Application Hang" → the process becomes unkillable
> and the box needs a reboot. The three "obvious" fixes all fail.
