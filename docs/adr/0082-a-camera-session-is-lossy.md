---
title: "ADR-0082: A camera session is lossy, and losslessness is a different acquisition model"
status: "Accepted"
status_note: "D1-D6 implemented. D7 (capture-stream sequence) tracked by `#337`."
date: "2026-08-25"
authors: "@charles8051"
tags: ["architecture", "decision", "camera", "backpressure", "frame-delivery", "observability", "machine-vision", "contract"]
supersedes: ""
superseded_by: ""
depends_on: "0035-periphery-camera.md"
---

# ADR-0082: A camera session is lossy

## Status

Settles what `CameraSession` promises about frame delivery, and why
`BufferExhaustionPolicy` has never done anything.

**D1 through D6 are implemented** in
[#322](https://github.com/charles8051/periphery/issues/322), which also deleted the
producer-side channel read that was
[#323](https://github.com/charles8051/periphery/issues/323) rather than fixing it.
`BufferExhaustionPolicy` has two members, `FullMode` derives from the policy, eviction
runs through the channel's `itemDropped` callback, the session documents its own
lossiness, and `CameraSessionMetrics` reports `ProducerStalls` /
`ProducerStallTime`. D6 is a statement about what Periphery does not offer, so it holds
by construction; nothing was added.

**D7 is not implemented.** `V4l2CameraBackend` still discards `v4l2_buffer.sequence`,
so a Linux consumer cannot yet detect a gap in the driver's queued capture stream.
Tracked by [#337](https://github.com/charles8051/periphery/issues/337).

## Context

`BufferExhaustionPolicy` is a public enum with four members. All four behave
identically, because the code that distinguishes them cannot be reached.

`CameraSession.cs:392-397` builds the delivery channel with a hardcoded
`FullMode = BoundedChannelFullMode.Wait`. With the defaults — `BufferCount: 3`,
`QueueDepth: 1` — a slow consumer parks the producer inside `WriteAsync` at `:455`
while it still holds the third pooled buffer.

The switch at `:490-517` is then unreachable, but not because the pool always has a
buffer to give — holding the third one may well leave it with none. It is unreachable
because a producer parked in `WriteAsync` never attempts the next acquisition, so
`_pool.TryDeliver` is not called and the exhaustion question is never asked. The pool
can be empty and the policy still never consulted.

Measured against a real session with a 100 ms consumer body: 9.1 fps delivered,
`Metrics.FramesDropped` 0, meter 0, and all four policy values indistinguishable.

### The stall does not prevent loss, it hides it

This is the fact the whole decision turns on.

A camera is a real-time source. Photons keep arriving at the sensor whether or not
anyone is reading. When the producer parks, `IMFSourceReader::ReadSample` stops being
called and Media Foundation discards frames internally, uncounted. Backpressure is
coherent only when the producer can be told to slow down, and a webcam cannot be.

Four days of the kiosk logs: 1080p negotiated at **60 fps**, roughly **29.5 fps**
delivered, `FramesDropped` summed across every session in the window: **1**. Half the
frames were lost and the library counted one of them.

So `Wait` is not the lossless option. It is the option that moves the loss somewhere
Periphery cannot see, and then reports zero.

### The recorder argument does not survive this

An earlier framing held that the default was undecidable because a recorder wants
`Wait` while a preview wants drop. The conclusion — one default, and it drops — stands,
but the reasoning needs a distinction it did not draw.

**`Wait` genuinely wins for a burst shorter than the platform's own queue.** Media
Foundation and V4L2 both buffer several frames before they begin discarding, so a
consumer that stalls briefly and then catches up loses nothing by parking the producer:
the frames wait in the driver and arrive late rather than never. For that shape of
overload `Wait` really is lossless and dropping really does lose frames.

What `Wait` cannot survive is **sustained** overload. Once the consumer's average rate
falls below the source's, no amount of parking helps — the platform queue saturates and
begins discarding uncounted, and every other consumer is stalled meanwhile. A recorder
in that state is not getting every frame; it is getting an unknown subset and a frozen
preview.

So the axis is not consumer class, it is burst versus sustained, and the knob that
serves it is depth rather than mode. A queue deep enough to absorb the burst gets the
`Wait` benefit without the `Wait` failure mode.

### Two bounded stages in series is why the enum died

`BufferCount` bounds the pool. `QueueDepth` bounds the channel.
`BufferExhaustionPolicy` governs the first, `FullMode` the second. The channel is
shallower, so it fills first and parks the producer mid-cycle, before the next
acquisition. The pool's policy is then unreachable — not because the pool stays full,
but because nothing asks it.

That follows from the *defaults*, not from the design. Configure `QueueDepth` at or
above `BufferCount` and the producer can enqueue every pooled frame, attempt one more
acquisition, and get null — at which point `Options.ExhaustionPolicy` is consulted exactly as
written. The dead code is dead at `QueueDepth: 1`, which is simply the shipped value.

A frame is free, queued, or leased. That is one budget, currently modelled as two
independent bounds with two independent overflow policies.

### `DropIncoming` is the wrong drop

`DropIncoming` discards the newly arrived frame and keeps the older queued one. At
`QueueDepth: 1` that hands a slow consumer the stalest available frame, every time. Its
own doc comment claims "low latency", which is backwards. For a live source the newest
frame is almost always the useful one.

## Decision

**D1. A camera session is lossy by contract.** Frames may be dropped between the sensor
and the consumer, and a consumer must not assume it received every frame the camera
produced. This is stated in `CameraSession`'s own documentation, not left to be
discovered.

**D2. The default is latest-wins, and the enum shrinks to say only what is true.** When
the pipeline is full, the oldest undelivered frame is discarded and the newest kept.

`BufferExhaustionPolicy` becomes two members:

```csharp
public enum BufferExhaustionPolicy
{
    LatestWins,     // default: drop the oldest undelivered frame
    StallProducer,  // park the producer; see D4 for what that does and does not buy
}
```

`DropIncoming` and `AllocateOverflow` are **deleted**, not implemented. Neither has a
consumer. `DropIncoming` keeps the stalest frame, which is wrong for a live source and
whose doc comment claimed the opposite. `AllocateOverflow` defeats the pool's purpose to
avoid a drop the contract already permits under D1. Four members, all requiring tests and
none exercised, is a larger surface than the behaviour justifies — a public enum whose
values are untested is worse than a smaller enum that is.

`DropOldestQueued` is renamed `LatestWins`. That is what it does, and it is the name
`FrameFlow.Graph`'s `EdgeOptions.LatestWins()` already uses for the same semantics.
Matching the vocabulary is deliberate: a consumer bridging frames into that runtime
should not have to translate between two names for one idea.

**D3. One budget, one policy, and the eviction disposes what it drops.** The channel's
overflow behaviour derives from `Options.ExhaustionPolicy` rather than being hardcoded:
`LatestWins` maps to `BoundedChannelFullMode.DropOldest`, `StallProducer` to `Wait`.
Whether `QueueDepth` survives as a knob separate from `BufferCount` is an implementation
question for `#322`; the contract is that a frame is free, queued, or leased, and one
policy governs what happens when none are free.

A dropped frame must be disposed or its pooled buffer never returns. `BoundedChannel`
provides exactly that hook:

```csharp
Channel.CreateBounded<LeasedCameraFrame>(
    new BoundedChannelOptions(QueueDepth) { FullMode = /* from the policy */ },
    itemDropped: frame => frame.Dispose());
```

**Availability, checked rather than assumed.** The `itemDropped` parameter is present in
the `net8.0` reference assembly shipped with `Microsoft.NETCore.App.Ref` 8.0.30, and a
probe targeting `net8.0` compiled against it and ran on runtime 8.0.30. Behaviour was
measured on 8.0.30 and 10.0.11, across `DropOldest`, `DropWrite` and `DropNewest`, on
both `TryWrite` and `WriteAsync`: the callback receives the exact evicted item, on the
producer's own thread, outside the channel lock.

An earlier revision claimed the overload "has existed since .NET 6". That was an
unverified claim about version history and is withdrawn — what is verified is the
paragraph above. Note the implication for CI: the API's presence was confirmed against
an 8.0.30 reference pack, so a build machine on an older .NET 8 SDK should be checked
before relying on it.

That settles the mechanism. Latest-wins is `FullMode = DropOldest` plus
`itemDropped: Dispose`. No stranded lease, no lock on the delivery path, no second
reader, and `SingleReader = true` stays honest.

**An earlier revision of this ADR got this wrong and the error is worth recording.** It
asserted that no built-in drop mode could be used with pooled frames, on the grounds
that the channel "discards a reference and calls nothing" — and concluded that eviction
had to be hand-written as a producer-owned `Interlocked.Exchange` slot. That conclusion
was reached by reasoning about the drop modes from their names without checking the
constructor overloads, and it would have produced a bespoke delivery structure to
replace a two-argument change. It also cited `CameraPreview` and the kiosk consumer's
`CameraFrameRouter` as independent precedent for the hand-rolled shape; both citations
were wrong. `CameraPreview` exchanges `PreviewSurface` objects and recycles them, which
is a surface pool and not frame eviction at all, and `CameraFrameRouter` implements the
producer-side channel read this ADR rejects — see `#323`.

**D4. `BlockProducer` becomes `StallProducer`, and its documentation says what it
does.** It stalls the producer. On a live capture source it does not guarantee delivery;
it converts countable drops into uncountable ones. It survives the D2 cull, as one of two members, because it is the correct choice for a
burst shorter than the platform's own queue — see the recorder discussion — and because a
future demand-paced backend (D6) could honour it meaningfully.

**D5. The stall is instrumented.** Time parked in `WriteAsync`, or a count of stall
entries. Today a stalled pipeline and a genuinely slow camera are indistinguishable from
outside, which is the actual defect behind `#322` — not the enum, but the absence of the
number that would have made the enum's deadness visible.

**D6. Where an application cannot guarantee keeping up with a free-running source,
losslessness needs demand-paced acquisition, which Periphery does not offer.** A
free-running camera is perfectly lossless when the consumer sustains the source rate, or
when buffering absorbs the bursts — that is the ordinary case and nothing here changes
it. The problem is only the one this ADR is about: what happens when the pipeline cannot
keep up.

For that case no overflow policy helps, because the loss has already moved upstream. A
camera that produces only when triggered removes the case instead of managing it, since
oversubscription cannot arise when production is paced by demand. That is a different
acquisition model, not a different policy setting.

Periphery does not have this and this ADR does not add it. Recorded so that nobody
builds an inspection application on `StallProducer` believing otherwise.

**D7. Report the capture-stream sequence where the platform provides one.** Periphery
discards evidence it already has: `V4l2Interop.cs:251` declares `v4l2_buffer.sequence`
and `V4l2CameraBackend` never reads it.

Be precise about what it proves. `v4l2_buffer.sequence` is assigned by the **driver** as
it queues capture buffers, not by the sensor. A driver or device that decimates before
queueing produces contiguous sequence values across frames the sensor did expose, and a
stream restart resets the counter. So it detects gaps in the driver's queued capture
stream, which is where Periphery's own losses land — it does not establish
source-level losslessness and must not be documented as if it did.

Media Foundation exposes no reliable per-sample sequence, so Windows gets the honest
answer that it cannot know. That asymmetry has to be representable; see `#337`.

## Consequences

- `BufferExhaustionPolicy` becomes load-bearing for the first time. All four values are
  currently untested in effect, because none of them had one.
- Changing the default from `DropIncoming` to latest-wins changes which frame a slow
  consumer sees. No known consumer depends on receiving the older frame.
- `#323` becomes reachable. `DropOldestQueued` reads the channel from the producer thread
  against `SingleReader = true`, which is latent only because the branch is dead. D3
  activates it, so the two must land together or `#323` first.
- A stalled pipeline becomes diagnosable. The kiosk's 60-negotiated / 29.5-delivered gap
  currently has no signal attached to it at all.
- Frame-gap detection is per-platform and asymmetric. Linux can answer, Windows cannot,
  and the API has to say so rather than returning a plausible zero.

## What would reverse this

A demand-paced backend. Industrial cameras — GigE Vision, USB3 Vision — support
hardware and software triggering natively, and in trigger mode production is paced by
the part arriving rather than by a free-running sensor. Losslessness then holds by
construction and the overflow policy stops mattering.

If that is ever built, prefer **GigE Vision**. Its wire protocol and device model are
standardised — UDP with standardised bootstrap registers — so one implementation of the
protocol serves Windows, Linux and macOS. Host integration is still platform-dependent:
packets traverse the NIC, its driver and the OS network stack, and vendors ship optional
filter drivers for throughput. The claim is not that the OS is absent, only that the
part Periphery would write is shared rather than triplicated, which is the opposite of
the UVC situation where MF and V4L2 are separate backends by necessity. USB3 Vision carries more
platform and vendor integration burden — implementations exist over standard user-mode
USB access, but vendor drivers and SDKs are commonly needed for particular devices or
for throughput, and that work does not transfer between platforms the way a protocol
implementation does.

The first move would be wrapping one vendor SDK for one specific camera, deliberately
throwaway. Implementing GenICam in general — GenTL producers as vendor `.cti` binaries,
GenApi as an XML feature-tree interpreter, no good .NET binding — is a separate project
and not a prerequisite for anything here.
