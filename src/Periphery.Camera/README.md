# Periphery.Camera

Frame capture from a camera you chose by identity, on Media Foundation and V4L2.

```sh
dotnet add package Periphery.Camera --prerelease
```

`Periphery.Camera` opens a camera that [`Periphery`](https://github.com/charles8051/periphery)
enumerated, negotiates a format against what the device actually advertises, and
hands you frames whose rows are tight and whose buffers come from a pool.

> **Capture is Windows and Linux.** Periphery enumerates cameras on all three
> platforms, but `CameraDevice` and `CameraSession` ship Media Foundation and
> V4L2 backends only, and throw `PlatformNotSupportedException` on macOS. The
> AVFoundation backend is planned, not written.

```csharp
using Periphery;
using Periphery.Camera;

var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithUsbId("046D", "0825")
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("That camera is not attached.");

await using var session = await CameraSession.For(device)
    .PreferNv12()
    .MaxResolution(1280, 720)
    .OpenAsync();

await foreach (var frame in session.CaptureAsync())
{
    using (frame)
        Process(frame.ContiguousBuffer.Span);
}
```

`using (frame)` is not optional. Every delivered frame holds a lease on a pooled
buffer, and a frame you do not dispose is a buffer the pool never gets back.

---

## The delivery contract

**A session is lossy, and says so.** You will not receive every frame the sensor
produced. Frames are lost in two places:

- **Upstream of the session**, when the producer is not calling the platform's
  read because it is parked writing into a full delivery queue. Counted as
  `CameraSessionMetrics.ProducerStalls` and `.ProducerStallTime`.
- **In the delivery queue**, when a frame is evicted to make room for a newer
  one. Counted as `FramesDropped`.

Design for gaps. If you need to know how large a gap was, timestamp deltas are
the signal, not a frame counter.

## Sizing the pool

Three knobs, one budget. The session pre-allocates `BufferCount + QueueDepth + 1`
buffers.

| Option | Means | Default |
|---|---|---|
| `BufferCount` | How many frames **you** may hold at once | 3 |
| `QueueDepth` | Channel capacity between producer and consumer, and buffers reserved for it | 1 |
| `ExhaustionPolicy` | `LatestWins` or `StallProducer` | `LatestWins` |

The `+ 1` is the producer's spare, and it is what makes `LatestWins` true rather
than aspirational. Eviction belongs to the channel, the channel only evicts on a
write, and a write needs a buffer to copy into.

`BufferCount` is the number that matters for everything below. Holding more
frames than it allows is the one loss no policy prevents, because active leases
are never revoked.

---

## Fan-out: one camera, several consumers

`Periphery.Camera` **does not ship a router**, deliberately. A preview, an
inference graph and an encoder want different latency, different depths and
different drop behaviour, and any type that picks for you is a type you end up
adapting away from. What the session gives you instead is refcounting, which is
enough to build exactly the fan-out you want in about fifteen lines.

If you are already using
[FrameFlow](https://github.com/charles8051/frame-flow), stop here and use
`FrameFlow.Graph`: one `OutputPort` connects to many `InputPort`s, each edge
carries its own capacity and overflow policy, and a branch that needs an
independent copy gets a per-edge cloner. Periphery cannot depend on it — the
refcounting protocol that would require is exactly what ADR-0045 removed from
this package — but a hand-rolled router next to a graph is a second
implementation of something already solved.

For everyone else, the recipe is a producer loop and one bounded channel per
consumer.

```csharp
using System.Threading.Channels;

// One channel per consumer. Depth and drop behaviour are per-consumer choices.
static Channel<ICameraFrame> Subscribe(int depth, BoundedChannelFullMode mode) =>
    Channel.CreateBounded<ICameraFrame>(
        new BoundedChannelOptions(depth) { FullMode = mode, SingleWriter = true },
        itemDropped: static f => f.Dispose());   // ← the load-bearing argument

var preview   = Subscribe(1, BoundedChannelFullMode.DropOldest);
var inference = Subscribe(1, BoundedChannelFullMode.DropOldest);
var encoder   = Subscribe(8, BoundedChannelFullMode.DropWrite);
Channel<ICameraFrame>[] subs = [preview, inference, encoder];

try
{
    // The producer loop: one AddRef per subscriber, then release your own lease.
    await foreach (var frame in session.CaptureAsync(ct: ct))
    {
        foreach (var sub in subs)
        {
            var lease = frame.AddRef();
            if (!sub.Writer.TryWrite(lease))
                lease.Dispose();  // channel completed — see below; drop modes return true
        }
        frame.Dispose();          // our own lease
    }
}
finally
{
    // Tell every consumer no more frames are coming, so ReadAllAsync ends
    // instead of hanging. Frames already queued are still theirs to dispose.
    foreach (var sub in subs)
        sub.Writer.TryComplete();
}
```

Each consumer then owns disposal of what it reads, and drains what is left:

```csharp
try
{
    await foreach (var frame in preview.Reader.ReadAllAsync(ct))
        using (frame)
            Render(frame);
}
finally
{
    // A cancelled or faulted consumer still owns whatever is queued. Completing
    // a channel does NOT dispose its contents and does NOT invoke itemDropped —
    // those frames are simply still readable, and still holding pooled leases.
    while (preview.Reader.TryRead(out var stranded))
        stranded.Dispose();
}
```

**`itemDropped` is the part that is easy to miss and expensive to omit.** Without
it, every dropped frame strands a pooled lease, and the pool is dead after
`BufferCount` drops. The overload is available on both of this package's target
frameworks, `net8.0` and `net10.0`.

**`itemDropped` covers eviction on a write, and nothing else.** It does not fire
on `Complete()`, on a cancelled read, or on a `WriteAsync` that throws. Every one
of those paths is yours to drain, which is why the two loops above have a
`finally`.

### Choosing a policy per consumer

`TryWrite` never waits, so the mode decides *which* frame is lost when a
consumer falls behind, not whether one is:

| Consumer | `FullMode` | Depth | Behaviour when full |
|---|---|---|---|
| Preview | `DropOldest` | 1 | Evicts the queued frame; the newest always wins. A stale frame is worth nothing on screen |
| Inference | `DropOldest` | 1 | Same. Detection on every third frame is still detection |
| Encoder | `DropWrite` | 8 | Keeps the queued burst intact and drops the incoming frame, so an encoded clip has no reordering |

Both route the loser to `itemDropped`, so the lease is released either way.
`DropOldest` chooses freshness, `DropWrite` chooses continuity.

Measured on a capacity-1 channel, writing `A` then `B`:

| `FullMode` | 2nd `TryWrite` | `itemDropped` | Left in channel |
|---|---|---|---|
| `DropOldest` | `true` | `A` | `B` |
| `DropWrite` | `true` | `B` | `A` |
| `Wait` | `false` | — | `A` |

**Under a drop mode `TryWrite` returns `true`, so the `if (!TryWrite)` branch in
the loop above never runs.** It is there for the one case that does return
`false` without invoking `itemDropped`: a completed channel, which is what a
consumer that has already shut down looks like. Disposing there is not a
double-release, because `itemDropped` did not fire.

### If a consumer must not lose frames

`BoundedChannelFullMode.Wait` does nothing under `TryWrite` — a full channel
simply refuses the write, which is `DropWrite` that forgot to call
`itemDropped`. To actually apply backpressure you have to await it, and then you
own the failure paths:

```csharp
var lease = frame.AddRef();
try
{
    await sub.Writer.WriteAsync(lease, ct);   // ownership transfers only on success
}
catch
{
    lease.Dispose();   // cancelled, or the channel completed — itemDropped did not fire
    throw;
}
```

The `try` is not defensive padding. `AddRef` runs before the await, and
`WriteAsync` throws `OperationCanceledException` on a cancelled wait and
`ChannelClosedException` on a completed channel — both **without** invoking
`itemDropped`. Written as a one-liner, every shutdown strands a pooled lease, and
enough shutdowns exhaust the pool.

Awaiting also blocks the shared producer loop, so a slow consumer now costs the
preview and the inference graph their frames too, and stalls the session's own
producer behind them. Use it only when a gap is worse than a stall for *every*
consumer. Otherwise give the lossless consumer its own copy-out path — see the
retention trap below.

### Size `BufferCount` to the fan-out

This is the step the recipe does not do for you. **Every queued frame in every
subscriber channel is a held lease**, so the leases outstanding at once are
bounded by the sum of the depths, plus whatever each consumer holds while
processing:

```
BufferCount ≥ Σ(subscriber depths) + (one in-flight frame per consumer)
```

For the three consumers above that is `(1 + 1 + 8) + 3 = 13`, not the default 3.
Leave it at 3 and the pool empties almost immediately, the producer cannot
acquire a buffer for the next frame, and you get a stall that looks like a slow
camera. The pool allocation is `BufferCount + QueueDepth + 1`, so this is a real
memory decision: 15 buffers at 1080p NV12 is about 47 MB.

The deep channel dominates the sum, which is the argument for keeping such
depths modest — or for having that consumer copy out of the pool instead, so its
backlog costs its own memory rather than the shared budget.

---

## The retention trap

**A consumer that keeps frames beyond the immediate callback cannot hold pooled
leases.** A pre-roll ring, a replay buffer, an "last N seconds" clip — anything
that retains — is bounded one-for-one by `BufferCount`, because every retained
frame is a lease the pool cannot reuse.

Raising `QueueDepth` does not fix this and makes it worse: it grows the queue's
own reservation without changing what you are allowed to hold.

The answer is to copy out of the pool:

```csharp
var ring = new Queue<OwnedCameraFrame>();

await foreach (var frame in session.CaptureAsync(ct: ct))
{
    using (frame)
        ring.Enqueue(frame.Copy());   // un-pooled; the lease is released on dispose

    while (ring.Count > capacity)
        ring.Dequeue().Dispose();     // evict oldest, free its memory
}
```

`Copy()` returns an `OwnedCameraFrame` that owns its own memory and is invisible
to the pool's accounting. That is the trade: the pool stops being your bound, and
your own budget becomes one you have to know.

Budget the ring before you build it. Two seconds at 30 fps is 60 frames:

| Format | Per frame @ 720p | 60-frame ring |
|---|---|---|
| NV12 | 1.3 MB | ~79 MB |
| BGRA32 | 3.5 MB | ~211 MB |

Cap the ring by count, evict from the front, and dispose what you evict.

---

## Related packages

| Package | What it adds |
|---|---|
| [`Periphery.Camera.Avalonia`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Avalonia) | A `CameraPreview` control that renders frames into a reused `WriteableBitmap` |
| [`Periphery.Camera.OpenCvSharp`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.OpenCvSharp) | `frame.ToBgr()` — any capture format as an OpenCV `Mat`, no `VideoCapture` |
| [`Periphery.Camera.Testing`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Testing) | A hardware-free backend for testing capture code, with patterned, multi-plane and padded frames |

## Further reading

- [ADR-0081](https://github.com/charles8051/periphery/blob/main/docs/adr/0081-a-delivered-frame-has-tight-rows.md) — every delivered frame has tight rows, and `Stride == BytesPerRow`
- [ADR-0082](https://github.com/charles8051/periphery/blob/main/docs/adr/0082-a-camera-session-is-lossy.md) — the delivery contract, and the buffer budget above
- [ADR-0045](https://github.com/charles8051/periphery/blob/main/docs/adr/0045-substrate-independence-from-crossbar.md) — why `ICameraFrame` inherits nothing from a pipeline framework
- [ADR-0065](https://github.com/charles8051/periphery/blob/main/docs/adr/0065-camera-testing-seam.md) — the testing seam
- [`docs/patterns/integration-package-placement.md`](https://github.com/charles8051/periphery/blob/main/docs/patterns/integration-package-placement.md) — where a third-party integration belongs
