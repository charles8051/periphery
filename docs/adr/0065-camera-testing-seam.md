---
title: "ADR-0065: Periphery.Camera.Testing — a supported hardware-free test seam for camera consumers"
status: "Accepted"
status_note: "Shipped - `src/Periphery.Camera.Testing`."
date: "2026-07-18"
authors: "@charles8051"
tags: ["architecture", "decision", "camera", "testing", "test-support", "in-memory-backend", "periphery-camera"]
supersedes: ""
superseded_by: ""
---

# ADR-0065: Periphery.Camera.Testing — a supported hardware-free test seam for camera consumers

## Status

> Number `0065` is provisional until merge. Resolves [periphery#146](https://github.com/charles8051/periphery/issues/146).

> **Amendment (2026-08-25, [periphery#321](https://github.com/charles8051/periphery/issues/321)).**
> Decision 1's hook list gains `FrameFactory` (with the `CameraFramePatterns`
> generators and the `CameraFrameSpec` / `CameraFramePlaneSpec` values it is
> handed) and `OverrideStride`, so a test can put known bytes at known offsets
> and can force padded rows. Frame geometry now routes through
> `CameraFrameLayout` and `Internal.PlaneLayout` rather than a bytes-per-pixel
> table of the fake's own, which had drifted: NV12 and NV21 were generated at 8
> bits/px, I420 / YV12 / MJPEG / Unknown at 24, and multi-plane frames were
> unreachable. The seam's shape is unchanged — the hooks are optional and the
> default frame is still a constant fill.

## Context

`Periphery.Camera`'s platform I/O contract — `Periphery.Camera.Internal.ICameraBackend`
(Media Foundation, V4L2, and a planned AVFoundation implementation) — is
deliberately **internal**. Per the repo stance, "device backends are a design
space, not a frozen surface": renames and abstraction-boundary moves are expected,
so the contract must not become public and load-bearing.

Periphery's own tests still exercise a real `CameraSession` without hardware:
`TestHelpers.InstallTestBackendFactory()` sets the internal
`CameraDevice.BackendFactory` hook to return an internal `TestCameraBackend` (a
scriptable fake), and `CameraSession.For(deviceInfo).OpenAsync()` then opens
against that fake. This works only inside the test assembly, via
`InternalsVisibleTo="Periphery.Camera.Tests"`.

**Downstream consumers cannot reach any of that.** A consumer that *wraps*
`CameraSession` — opens it and runs its own capture pump around `CaptureAsync`,
handling `CameraTimeoutException`, teardown, and reconnect — has no hardware-free
way to test that pump. The public construction paths
(`CameraSession.OpenAsync` / `CameraSession.For(device).OpenAsync()`) go straight
to the OS capture stack, `CameraSession` is `sealed` with an `internal`
constructor, and the fake backend + factory hook are internal.

Concrete driver: the kiosk's `PeripheryCameraBackend` wraps
`CameraSession` and had a production incident where a wedged UVC stream
(`CameraTimeoutException`, device still enumerated) left capture dead for ~15h
and orphaned the session's producer task. The fix's *pure* decision logic could
be unit-tested, but the IO shell (pump-`finally` session disposal, cancellation
stop) could not — there was no way to open a `CameraSession` over a
scripted/faulting frame source from outside Periphery.

## Decision

Ship a supported, packable **`Periphery.Camera.Testing`** library that exposes a
hardware-free capture backend and the wiring to drive `CameraSession` /
`CameraDevice` with it — while keeping the platform I/O contract internal.

1. **`InMemoryCameraBackend` (public)** implements the internal `ICameraBackend`
   **explicitly**. The interface (and `RawCameraFrame`) therefore stay off the
   public surface and free to evolve; the only public API is the
   configuration/observation surface: advertised formats/controls, synthetic
   frames, and the failure modes real drivers exhibit — `FaultOnOpen`,
   `FaultOnNextRead`, `HangOnRead`, `MaxFrames`, `FrameDelay`,
   `OverridePixelFormat`, plus `IsOpen` / `IsCapturing` / `IsDisposed` /
   `FrameCounter` / `ReadHangReached` observation (all read through `Volatile`,
   since capture flips them from the producer thread while the test polls).
   An instance models **one** device lifecycle: once disposed it stays disposed
   and a second `OpenAsync` throws `ObjectDisposedException`, matching the real
   backends, where the factory mints a fresh backend per open. Multi-open paths
   therefore need a per-open factory, not a shared instance.

2. **`CameraTestScope` (public `IDisposable`)** redirects
   `CameraDevice.BackendFactory` to the fake for the scope's lifetime and restores
   it on dispose. This is the seam for code that opens from a `DeviceInfo` itself
   (`CameraSession.For(deviceInfo).OpenAsync()`) — there is no argument to hand a
   backend to, so the global factory hook is the only interception point. The
   `Install(Func<DeviceInfo, InMemoryCameraBackend>)` overload (a fresh backend
   per open) is the general form; `Install(InMemoryCameraBackend)` is a
   single-open convenience for inspecting the one backend afterwards.

3. **`CameraTestHarness` (public static)** constructs a `CameraDevice` /
   `CameraSession` directly over a backend with **no** global-state redirect, for
   code that is handed an already-open session.

4. `Periphery.Camera` grants `InternalsVisibleTo="Periphery.Camera.Testing"` so the
   package can implement the internal interface and reach the internal factory
   hook and construction ctors.

5. **Dogfood:** Periphery's own camera suite consumes the package — it uses
   `InMemoryCameraBackend` by name and its helpers delegate to
   `CameraTestHarness` — so there is one fake, not two, and the suite reads the
   way a downstream consumer's does.

6. **The package is version-locked to its host.** Because it binds to
   `Periphery.Camera`'s internals, `Periphery.Camera.Testing` packs an **exact**
   dependency range (`[x.y.z]`), not NuGet's default minimum range. See the
   trade-off below.

`FakeTimeProvider` support already exists on `CameraSession`
(ADR-0052), so a consumer can combine `HangOnRead` with a fake clock to drive the
frame-timeout deterministically — the exact wedge scenario above.

## Consequences

### Positive

- Downstream consumers can unit-test `CameraSession`-wrapping capture pumps —
  including faults, wedged-stream timeouts, and teardown — with no camera.
- The platform I/O contract (`ICameraBackend` / `RawCameraFrame`) stays internal
  and free to evolve; only a small, intention-revealing test API is public.
- One shared fake across Periphery's own suite and downstream consumers — no
  drift between two parallel implementations.

### Negative / trade-offs

- A new packable assembly to build and publish alongside `Periphery.Camera`.
- `CameraTestScope`'s redirect is **process-global** mutable state; overlapping
  scopes across parallel tests clobber each other. Consumers must serialize such
  tests (a single non-parallel collection), the same constraint Periphery's own
  camera suite already honors. `CameraTestHarness` is the global-state-free
  alternative when the code under test accepts a session directly.
- `CameraTestScope`'s single global slot also means **out-of-order disposal does
  not fully unwind**: an inner scope disposed after the outer one restores the
  outer's (already-disposed) factory rather than the pre-scope state. Dispose in
  LIFO order — a `using` block does this for you.
- **An internals-bound support package is version-locked to its host by
  construction.** A default `ProjectReference` packs as `version="x.y.z"`, which
  NuGet reads as `>= x.y.z`, so a consumer that pins `Periphery.Camera` for the
  product and lets the test package float can restore a mismatched pair and then
  fail at *test runtime* with `TypeLoadException` / `MissingMethodException` —
  the worst place to fail for a package whose job is making tests trustworthy.
  `Periphery.Camera.Testing.csproj` therefore rewrites the packed range to the
  exact `[x.y.z]` (a `PinExactCameraDependency` target over
  `_ProjectReferencesWithVersions`), turning the mismatch into a restore error.
  The cost is that the two packages must be released in lockstep — which is the
  honest description of the coupling, not a new constraint.

## Alternatives considered

- **Make `ICameraBackend` / `RawCameraFrame` public.** Rejected: it freezes the
  backend design space this repo explicitly keeps fluid, for no gain — consumers
  need a *ready-made* configurable fake, not the ability to author their own
  backend.
- **Grant `InternalsVisibleTo` to each consumer test assembly.** Rejected: doesn't
  scale, and it leaks the entire internal surface rather than a curated one.
- **A frame-source injection point on `CameraSessionBuilder`.** Deferred: a larger
  public-surface change than the need warrants; the fake-backend seam already
  covers hardware-free capture, faults, and timeouts. Can supersede this ADR later
  if a narrower value-typed frame source proves more ergonomic.
