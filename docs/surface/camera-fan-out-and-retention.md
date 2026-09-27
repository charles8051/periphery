# Camera fan-out and retention

How to feed several consumers from one `CameraSession`, and how to keep frames beyond the capture loop.
The contract behind both is [ADR-0082](../adr/0082-a-camera-session-is-lossy.md).

## The delivery contract

A session is lossy. Frames are lost in two places:

- **Before the session**, when the producer is not reading from the platform because it is waiting on a
  full delivery queue. Counted as `CameraSessionMetrics.ProducerStalls` and `ProducerStallTime`.
- **In the delivery queue**, when a frame is evicted to make room for a newer one. Counted as
  `FramesDropped`.

Design for gaps. To measure a gap, use timestamp deltas, not a frame counter.

## The pool

The session pre-allocates `BufferCount + QueueDepth + 1` buffers.

| Option | Meaning | Default |
|---|---|---|
| `BufferCount` | Frames you may hold at once | 3 |
| `QueueDepth` | Delivery queue capacity, and the buffers reserved for it | 1 |
| `ExhaustionPolicy` | `LatestWins` or `StallProducer` | `LatestWins` |

The `+ 1` is the producer's spare. Eviction happens on a write, and a write needs a buffer to copy into.

Holding more than `BufferCount` frames starves the producer. No policy prevents it, because a held lease is
never revoked.

## Fan-out: one camera, several consumers

`Periphery.Camera` has no router. A preview, an inference graph and an encoder want different latency,
queue depth and drop behaviour. The session gives you refcounted frames instead, and the fan-out is a
producer loop plus one bounded channel per consumer.

If you already use [FrameFlow](https://github.com/charles8051/frame-flow), use `FrameFlow.Graph` instead: one
`OutputPort` connects to many `InputPort`s, and each edge has its own capacity and overflow policy.

```csharp
using System.Threading.Channels;

// One channel per consumer. Depth and drop behaviour are per-consumer choices.
static Channel<ICameraFrame> Subscribe(int depth, BoundedChannelFullMode mode) =>
    Channel.CreateBounded<ICameraFrame>(
        new BoundedChannelOptions(depth) { FullMode = mode, SingleWriter = true },
        itemDropped: static f => f.Dispose());   // releases the lease of every evicted frame

var preview   = Subscribe(1, BoundedChannelFullMode.DropOldest);
var inference = Subscribe(1, BoundedChannelFullMode.DropOldest);
var encoder   = Subscribe(8, BoundedChannelFullMode.DropWrite);
Channel<ICameraFrame>[] subs = [preview, inference, encoder];

try
{
    // One AddRef per subscriber, then release your own lease.
    await foreach (var frame in session.CaptureAsync(ct: ct))
    {
        foreach (var sub in subs)
        {
            var lease = frame.AddRef();
            if (!sub.Writer.TryWrite(lease))
                lease.Dispose();  // the consumer closed its channel
        }
        frame.Dispose();
    }
}
finally
{
    // Tell every consumer no more frames are coming, so ReadAllAsync ends.
    foreach (var sub in subs)
        sub.Writer.TryComplete();
}
```

Each consumer disposes what it reads, and closes its channel before draining it:

```csharp
try
{
    await foreach (var frame in preview.Reader.ReadAllAsync(ct))
        using (frame)
            Render(frame);
}
finally
{
    // Close first, so the producer's next TryWrite fails and it disposes that frame.
    preview.Writer.TryComplete();

    // Then drain. Completing a channel does not dispose its contents or call itemDropped.
    while (preview.Reader.TryRead(out var stranded))
        stranded.Dispose();
}
```

Draining before completing is a race: the drain empties the channel, the producer writes one more frame,
and that frame's lease is never released.

Without `itemDropped`, every dropped frame keeps its lease, and the pool is empty after `BufferCount`
drops. `itemDropped` fires only for eviction on a write. It does not fire on `Complete()`, on a cancelled
read, or on a `WriteAsync` that throws. Those paths are why both loops above have a `finally`.

### Choosing a policy per consumer

`TryWrite` never waits, so the mode decides which frame is lost when a consumer falls behind:

| Consumer | `FullMode` | Depth | When full |
|---|---|---|---|
| Preview | `DropOldest` | 1 | Evicts the queued frame; the newest wins |
| Inference | `DropOldest` | 1 | Same |
| Encoder | `DropWrite` | 8 | Keeps the queued burst and drops the incoming frame, so a clip has no reordering |

Both modes route the lost frame to `itemDropped`. On a capacity-1 channel, writing `A` then `B`:

| `FullMode` | 2nd `TryWrite` | `itemDropped` | Left in channel |
|---|---|---|---|
| `DropOldest` | `true` | `A` | `B` |
| `DropWrite` | `true` | `B` | `A` |
| `Wait` | `false` | — | `A` |

Under a drop mode `TryWrite` returns `true`, so the `if (!TryWrite)` branch in the producer loop runs only
for a completed channel. `itemDropped` did not fire there, so disposing is not a double release.

### A consumer that must not lose frames

`BoundedChannelFullMode.Wait` does nothing under `TryWrite`: a full channel refuses the write without
calling `itemDropped`. To apply backpressure, await the write and handle its failures:

```csharp
var lease = frame.AddRef();
try
{
    await sub.Writer.WriteAsync(lease, ct);   // ownership transfers only on success
}
catch
{
    lease.Dispose();   // cancelled, or the channel completed; itemDropped did not fire
    throw;
}
```

`WriteAsync` throws `OperationCanceledException` on a cancelled wait and `ChannelClosedException` on a
completed channel, both without calling `itemDropped`.

Awaiting blocks the shared producer loop, so one slow consumer costs every other consumer its frames and
stalls the session. Use it only when a gap is worse than a stall for every consumer. Otherwise give the
lossless consumer its own copy, as in [Retention](#retention).

### Size `BufferCount` to the fan-out

Every queued frame in every subscriber channel is a held lease:

```
BufferCount ≥ Σ(subscriber depths) + (one in-flight frame per consumer)
```

For the three consumers above that is `(1 + 1 + 8) + 3 = 13`, not the default 3. With 3, the pool empties,
the producer cannot get a buffer, and the stall looks like a slow camera. The allocation is
`BufferCount + QueueDepth + 1`: 15 buffers at 1080p NV12 is about 47 MB.

The deep channel dominates the sum. Keep such depths modest, or have that consumer copy out of the pool.

## Retention

A consumer that keeps frames after the loop body, such as a pre-roll ring or a replay buffer, is bounded by
`BufferCount`, because every retained frame is a lease. Raising `QueueDepth` does not help; it grows the
queue's reservation, not what you may hold.

Copy out of the pool instead:

```csharp
var ring = new Queue<OwnedCameraFrame>();

await foreach (var frame in session.CaptureAsync(ct: ct))
{
    using (frame)
        ring.Enqueue(frame.Copy());   // un-pooled; the lease is released on dispose

    while (ring.Count > capacity)
        ring.Dequeue().Dispose();     // evict the oldest
}
```

`Copy()` returns an `OwnedCameraFrame` that owns its memory and is outside the pool's accounting. Budget it
yourself. Two seconds at 30 fps is 60 frames:

| Format | Per frame @ 720p | 60-frame ring |
|---|---|---|
| NV12 | 1.3 MB | ~79 MB |
| BGRA32 | 3.5 MB | ~211 MB |
