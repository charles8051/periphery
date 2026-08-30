# Periphery.Camera baseline — 2026-05-08

First measured baseline for the `Periphery.Camera` package. Captured
right after [ADR-0040 §4](../adr/0040-camera-ergonomic-roadmap.md)
selectors+sinks+builder landed and the logging/diagnostics standards
were applied. Future measurements should land as **new** snapshots in
this folder rather than overwriting this one — the value is in the
diff over time.

Run via `cd benchmarks/Periphery.Camera.Benchmarks && dotnet run -c Release`
(see the [benchmark project's README](../../benchmarks/Periphery.Camera.Benchmarks/README.md)
for filter syntax and what each class measures).

## Environment

| Field | Value |
|---|---|
| Date | 2026-05-08 |
| Commit | `b0ba0d4` (`feat(benchmarks): BenchmarkDotNet baseline for Periphery.Camera`) |
| BenchmarkDotNet | 0.14.0 |
| OS | Windows 11 (10.0.26200.8246) |
| CPU | AMD Ryzen 7 5800X (8-core, X64, AVX2) |
| .NET SDK | 10.0.300-preview.0.26177.108 |
| Host runtime | .NET 8.0.26, RyuJIT |
| Job | DefaultJob (server GC, concurrent GC, Release config) |

The synthetic `BenchmarkCameraBackend` is used throughout — these
numbers measure **library overhead only**, not real driver / interop
costs. See the benchmark project README for the rationale.

## Results

### `CameraFramePoolBenchmarks` — pool round-trip

`TryDeliver(in raw)` + `LeasedCameraFrame.Dispose()` on a primed
single-buffer pool. Pure pool overhead — no channel, no producer
thread, no backend.

| Resolution (NV12) | Frame size | Mean | StdDev | Allocated |
|---|---:|---:|---:|---:|
| 720p (1280×720)  |  1.4 MB | 64.62 µs |  1.95 µs | 168 B |
| 1080p (1920×1080) |  3.1 MB | 175.91 µs | 12.99 µs | 168 B |
| 4K (3840×2160)   | 12.4 MB | 633.37 µs | 15.04 µs | 168 B |

The 168 B is the `LeasedCameraFrame` wrapper itself — unavoidable in
the current ownership design (it's the thing the consumer disposes).
Throughput tracks raw memcpy bandwidth: ~21 GB/s at 720p, ~17 GB/s at
1080p, ~19.6 GB/s at 4K. The buffer copy is the floor.

### `CameraSessionBenchmarks` — end-to-end pipeline

`ReadFrameAsync` from a primed session. Synthetic backend → producer
thread → bounded channel → consumer dequeue → metric increment →
lease handoff.

| Resolution (NV12) | Mean | StdDev | Theoretical fps ceiling | Allocated |
|---|---:|---:|---:|---:|
| 720p  | 68.32 µs | 0.67 µs | ~14,640 fps | 1.40 KB |
| 1080p | 147.89 µs | 1.46 µs |  ~6,760 fps | 1.41 KB |

The interesting result: at 1080p the consumer reads in **148 µs**
even though the buffer copy alone (per the pool benchmark) takes
**176 µs**. The pipeline architecture is paying off — the producer
thread amortizes the copy in parallel while the consumer is busy with
the previous frame. At 720p the pipeline overhead beyond the copy is
just **~3.7 µs** (channel queue + Task wrap + RecordFrame + metric).

### `FormatSelectorBenchmarks` — selector LINQ chains

Format selection over a 30-format list (5 resolutions × 3 pixel
formats × 2 frame rates). Runs once per session open via the builder.

| Method | Mean | StdDev | Gen0 | Allocated |
|---|---:|---:|---:|---:|
| `GoldenChain_PreferMjpeg_WithinBox`  | 266.8 ns | 10.97 ns | 0.0014 | 584 B |
| `FallbackChain_PreferMjpegThenAny`   | 304.0 ns | 12.74 ns | 0.0014 | 544 B |

Sub-microsecond. Even at 100 sessions/sec, format selection costs
~30 µs/sec total — not a real cost.

### `CameraFrameSinksBenchmarks` — `WriteContiguousToAsync` to `Stream.Null`

30 frames at 720p NV12, pipelined through the sink with no disk I/O
contribution.

| Method | Mean | StdDev | Allocated |
|---|---:|---:|---:|
| `WriteContiguousFramesToNullStream` | 2.491 ms | 0.0376 ms | 37.66 KB |

That's **~83 µs/frame** vs `ReadOneFrame` at 68 µs/frame — about
**15 µs/frame** of sink + `await foreach` overhead.

## Verdict

Performance is satisfactory for v1. The library is **two orders of
magnitude faster than the cameras it talks to**:

- Real USB cameras max out at 30–60 fps for 1080p. The library can
  pipeline-process **6,700 fps** of 1080p NV12.
- Real `MfCameraBackend.ReadRawFrameAsync` is **milliseconds** (COM
  marshalling + driver buffer handoff). Library overhead is in
  microseconds. Library cost is irrelevant compared to driver cost.
- Memory bandwidth (17–21 GB/s observed) matches what this CPU/RAM
  combo can do — we're not leaving performance on the table.

## Caveat: per-frame allocation is ~1.4 KB

It's not zero. Breakdown:

- 168 B is the `LeasedCameraFrame` wrapper (unavoidable; it's the
  thing the consumer disposes).
- ~1.2 KB is pipeline overhead: `Task<RawCameraFrame>` from the
  backend interface (struct returns can't be Task-cached), channel
  state-machine boxes, async-await machinery.

At 60 fps that's 84 KB/s of Gen0 pressure — the runtime laughs at
this. At 1,000 fps it's 1.4 MB/s — still Gen0 only.

If a future scenario demands truly zero-alloc steady state
(very-low-latency, embedded, etc.), the path is changing
`ICameraBackend.ReadRawFrameAsync` to `ValueTask<RawCameraFrame>`
and pooling the channel-internal task wrappers. Recorded as a
potential v2 optimization, not a current problem.

## What this snapshot doesn't measure

- **Multi-session** — two cameras in one process. Backends are
  independent; pool is per-session; meter is shared. Probably scales
  linearly but worth confirming when there's a use case.
- **Real backend cost** — the synthetic backend is the whole point;
  `MfCameraBackend.ReadRawFrameAsync` cost is a hardware-in-loop
  measurement, not a microbenchmark.
- **First-frame latency** — `GlobalSetup` opens the session and primes
  capture once; per-frame measurements exclude open-time.

## Adding a new snapshot

When the package surface or hot path changes meaningfully, run the
suite again and add a new file `YYYY-MM-DD-periphery-camera-<label>.md`
in this folder. Keep prior snapshots untouched so the trend is
auditable.
