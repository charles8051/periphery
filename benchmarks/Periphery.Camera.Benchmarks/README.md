# Periphery.Camera.Benchmarks

BenchmarkDotNet baseline for `Periphery.Camera`. Establishes per-frame
overhead for the lease/return path, the full session pipeline, and the
byte-level frame sinks — measured against a synthetic backend so results
isolate library overhead from driver/interop costs.

## Running

> Benchmarks must run in **Release** configuration. BDN refuses to start
> under Debug.

```bash
# All benchmarks. Takes a few minutes for full statistical convergence.
dotnet run -c Release

# A single benchmark class.
dotnet run -c Release -- --filter "*PoolBenchmarks*"

# A single method across all parameter values.
dotnet run -c Release -- --filter "*ReadOneFrame*"

# Smoke test (1 iteration, no warm-up — values are jittery, validates
# only that the benchmark *runs*).
dotnet run -c Release -- --job dry

# List every discovered benchmark.
dotnet run -c Release -- --list flat
```

Results land in `BenchmarkDotNet.Artifacts/results/` as `.md` (GitHub-flavored),
`.csv`, `.html`, and `.json`.

## What's measured

| Class                           | What it measures                                           |
|---|---|
| `CameraFramePoolBenchmarks`     | Pure pool overhead: `TryDeliver` + `LeasedCameraFrame.Dispose` round-trip with a primed single-buffer pool. The floor below which the full session pipeline cannot go. Parameterized on image height (720 / 1080 / 2160 NV12). |
| `CameraSessionBenchmarks`       | End-to-end per-frame overhead via `ReadFrameAsync` from a primed session: synthetic backend → producer thread → bounded channel → consumer dequeue → metric increment → lease handoff. Parameterized on image height (720 / 1080). |
| `CameraFrameSinksBenchmarks`    | Sink throughput via `WriteContiguousToAsync` into `Stream.Null` for 30 frames at 720p NV12. Measures pipeline + sink without disk I/O contribution. |
| `FormatSelectorBenchmarks`      | Format-selector LINQ chains over a 30-format list (5 resolutions × 3 pixel formats × 2 frame rates). Two patterns: strict-filter golden chain, and the fallback-chain (`PreferPixelFormat → ThenBy…`). Runs once per session open in the builder so its cost contributes to the open-time budget. |

## Synthetic backend

`Backends/BenchmarkCameraBackend.cs` implements `ICameraBackend` against a
single pre-allocated frame buffer. Every call to `ReadRawFrameAsync` returns
a `RawCameraFrame` pointing at the same buffer with a monotonically-increasing
timestamp. This is **not** a realistic camera — it isolates the library's
own pipeline overhead from anything a real driver would add.

The benchmarks use `CameraDevice.BackendFactory` (the same hook the
`Periphery.Camera.Tests` project uses for `InMemoryCameraBackend`) to swap the
synthetic backend in. `[InternalsVisibleTo]` on `Periphery.Camera` is
expanded to include `Periphery.Camera.Benchmarks` so the project can
implement the internal `ICameraBackend` interface.

## What's not measured

- **Real-driver overhead** — `MfCameraBackend.ReadRawFrameAsync` does
  COM interop, a buffer copy, and stride handling per frame. None of that
  shows up here; that's the `Periphery.Camera.Windows` integration test
  surface, not a microbenchmark.
- **Encoding cost** — by design (ADR-0040 §3). Encoding-bearing sinks
  belong above core; benchmarking them lives with the future
  `Periphery.Camera.Pipelines` package.
- **First-frame latency** — `GlobalSetup` opens the session and primes
  capture once; per-frame measurements exclude open-time costs. Open-time
  is a separate concern.

## Interpreting results

`[MemoryDiagnoser]` is on every class, so the `Allocated` column reports
managed allocation per operation. Steady-state lease/return should be
**zero or near-zero allocations** — the leased frame wrapper is the only
expected allocation per frame, and the buffer recycles.

If a future change introduces per-frame allocation regressions, the
allocated column is the canary.
