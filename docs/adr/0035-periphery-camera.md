---
title: "ADR-0035: Periphery.Camera — Native Camera I/O Extension"
status: "Accepted"
status_note: "Shipped - `src/Periphery.Camera`, Media Foundation (Windows) and V4L2 (Linux). The AVFoundation backend is still planned."
date: "2026-05-06"
authors: "@charles8051 (design)"
tags: ["architecture", "decision", "camera", "video", "extension", "api-design", "media-foundation", "v4l2", "avfoundation"]
supersedes: ""
superseded_by: ""
---

# ADR-0035: Periphery.Camera — Native Camera I/O Extension

## Context

`Periphery` core already exposes `DeviceCategory.Camera` and enumerates webcams as
zero-I/O `DeviceInfo` snapshots. That solves the discovery problem: "which camera-like
devices exist on this machine?" It does not solve the I/O problem: "open this camera,
select a format, capture frames, survive reconnects, and correlate the live capture
endpoint back to the discovered device."

The need for a dedicated camera package is structurally similar to the packages already
described by ADR-0019 (`Periphery.Usb`), ADR-0020 (`Periphery.Hid`), ADR-0023
(`Periphery.Midi`), ADR-0024 (extension package pattern), ADR-0026 (no hidden handle
opens in enrichers), and ADR-0027 (`DeviceProxyBase`). A camera package is therefore
not a special case; it is another Periphery I/O extension that must obey the same
discovery/I-O boundary and reconnect model.

Two design directions are plausible:

1. Build `Periphery.Camera` on the platform-native camera APIs.
2. Build `Periphery.Camera` on FFmpeg and treat the native APIs as implementation
   details or optional fallbacks.

The FFmpeg-first option is tempting because it offers broad codec and transport support,
but it is a poor match for the foundational responsibilities of this package.

### Problem 1 — Identity is authoritative at the native camera stack

The hardest part of camera integration is not decoding pixels; it is identifying the
correct physical device and mapping it back to a stable `DeviceInfo`.

The authoritative identity surfaces are platform-native:

| Platform | Primary camera stack | Stable endpoint identity |
|---|---|---|
| Windows | Media Foundation | symbolic link / device interface path |
| Linux | V4L2 | `/dev/video*` + sysfs / udev identity |
| macOS | AVFoundation | `AVCaptureDevice.UniqueID` |

These are the identifiers that the OS camera APIs understand and expose directly.
FFmpeg sits above these stacks and tends to flatten, hide, or backend-normalize the
exact identifiers needed for durable correlation. A FFmpeg-first design would therefore
force `Periphery.Camera` to recover backend-specific identity indirectly, defeating the
reason to use FFmpeg as the foundation in the first place.

### Problem 2 — Camera control is backend-specific, not codec-specific

The core camera responsibilities are:

- format enumeration (resolution, pixel format, frame rate)
- source configuration
- capture start / stop
- camera controls (exposure, focus, white balance, zoom, torch, etc.)
- device-loss detection and reconnect behaviour

These responsibilities live in Media Foundation, V4L2, and AVFoundation. FFmpeg is
excellent at frame transport, colorspace conversion, encoding, muxing, and network
streaming, but it is not the cleanest source of truth for device controls or hardware
identity.

### Problem 3 — FFmpeg adds exactly the wrong dependency at the lowest layer

Periphery core's zero-third-party-dependency rule is load-bearing. Extension packages
may be more flexible, but the foundational package for camera I/O still benefits from
staying close to the OS APIs. Making FFmpeg the root dependency of `Periphery.Camera`
would introduce a heavyweight native dependency at the exact layer where the package is
trying to model stable device identity and direct device capabilities.

### Problem 4 — Handle-gated camera metadata must remain explicit

ADR-0026 established that `IDeviceEnricher` must not open device handles. This matters
for cameras because many useful facts are handle-gated on at least one platform:
supported resolutions, frame-rate ranges, pixel formats, and control ranges often
require source activation (`IMFMediaSource`), a V4L2 file descriptor, or an open
`AVCaptureDevice` session. Those capabilities belong on the open camera handle or on an
explicit snapshot helper, not in `DeviceInfo`.

### Problem 5 — A camera package must be cross-platform at the abstraction layer

The abstraction must be honest across Windows, Linux, and macOS. The backend APIs are
not identical:

| Concern | Windows | Linux | macOS |
|---|---|---|---|
| Enumeration identity | MF symbolic link, SetupAPI/CfgMgr32 correlation | `/dev/video*`, sysfs, udev | `AVCaptureDevice.UniqueID`, IOKit / AVFoundation correlation |
| Open path | `IMFMediaSource` / source reader | `open()` + `VIDIOC_*` | `AVCaptureSession` / `AVCaptureDeviceInput` |
| Format model | media types | `v4l2_fmtdesc` + frame sizes + frame intervals | `AVCaptureDeviceFormat` |
| Control model | camera controls + media foundation attributes | V4L2 controls | `lockForConfiguration` + property APIs |
| Permission model | privacy policy / access denied | device node permissions | TCC prompt / `NSCameraUsageDescription` |

The public API must therefore expose only the capabilities that can be represented
cleanly across all three backends, while permitting backend-specific escape hatches only
where they are explicitly marked as platform-specific.

---

## Decision

Implement `Periphery.Camera` as a **native camera I/O extension package** that follows
the extension pattern from ADR-0024.

### Decision 1 — Native stacks are the primary backends

`Periphery.Camera` uses the OS camera stack directly:

- **Windows:** Media Foundation
- **Linux:** V4L2
- **macOS:** AVFoundation

These APIs are the primary source of device opening, format enumeration, camera
configuration, frame capture, and device-loss handling.

### Decision 1a — v1 ships Windows-only; Linux and macOS are deferred

**Amendment (2026-05-08).** Decision 1 stays as the eventual goal, but v1 of
`Periphery.Camera` ships with the Windows Media Foundation backend only. Linux
V4L2 and macOS AVFoundation are explicitly deferred to a future version.

The reasons for the scope cut:

- The Windows backend has been the only one with sustained implementation
  effort, and getting it production-ready (correctness, plane layout,
  reconnect, performance, format coverage) is the gating work for any
  higher-level extension built on top — most notably `Periphery.Camera.Pipelines`
  (ADR-0036). Splitting attention across three backends in v1 risks all three
  being half-baked.
- V4L2 is a meaningfully lower-level API than Media Foundation or
  AVFoundation. Adding it correctly (plane-aware buffers, control parity,
  reconnect handling, hot-plug semantics) is a multi-week project, not a
  port. The same is true to a lesser degree for AVFoundation, where macOS
  permission flows (TCC, `NSCameraUsageDescription`, app-bundle requirements)
  add real surface area beyond the AVF API itself.
- Deferring Linux/Mac does not change any cross-cutting decision in this
  ADR — frame ownership, snapshot helpers, session/host shapes, exception
  hierarchy, and pixel format set are all designed to be backend-neutral and
  apply unchanged when the additional backends land.

Concretely:

- `CameraDevice.CreateBackend` continues to throw
  `PlatformNotSupportedException` on non-Windows with a message naming the
  deferred backends. Consumers who target Linux/Mac get a clean failure at
  open time rather than a half-working surface.
- The package is published with a Windows-first `Description` and the README
  states the v1 platform scope explicitly.
- `Periphery.Camera.Pipelines` and any other extension built on top may rely
  on Windows-only behaviour in v1 without compromise. When Linux/Mac
  backends arrive, their backend-neutrality requirement returns.
- Re-enabling Linux/Mac is a future ADR (or an amendment to this one) — not
  something that lands in a feature branch silently.

This decision does **not** rule out community-contributed Linux/Mac
backends landing earlier than a v2; it scopes what `@charles8051` will
ship as the v1 baseline.

### Decision 2 — FFmpeg is not the foundation

FFmpeg is explicitly **not** the foundational backend for `Periphery.Camera`.

FFmpeg may be introduced later in one of two roles:

1. as an **optional companion package** (`Periphery.Camera.FFmpeg`) for encoding,
   muxing, RTSP/RTMP/network output, and pixel-format conversion; or
2. as an **adapter layer** that consumes `CameraFrame` output from `Periphery.Camera`
   rather than owning device discovery and identity itself.

This preserves the correct architectural layering: native camera APIs own device I/O and
identity; FFmpeg owns codec and transport concerns.

### Decision 3 — `DeviceInfo` is the shared passport

`Periphery.Camera` opens cameras from `DeviceInfo`, not from ad hoc strings or integer
indices. The core open factory accepts a `DeviceInfo` and the backend resolves it to the
native camera endpoint internally.

The correlation rule is:

- the caller supplies `DeviceInfo`
- the backend resolves the native endpoint using stable identity data
- the open `CameraDevice` retains both the original `DeviceInfo` and the backend-native
  endpoint identity for diagnostics and reconnect behaviour

The minimum matching contract is:

- prefer the platform-native stable endpoint ID when it can be mapped directly to
  `DeviceInfo.Id`
- otherwise fall back to `ContainerId`
- otherwise fall back to a composite of `VendorId`, `ProductId`, `SerialNumber`,
  `LocationPath`, and camera-specific backend identity

The caller never passes an OpenCV index, DirectShow moniker string, or raw FFmpeg input
URL to the core `OpenAsync` API.

### Decision 4 — Handle-gated capabilities use an explicit snapshot helper

Capabilities that require activating the camera stack belong behind an explicit call:

```csharp
public static Task<CameraSnapshot> ReadSnapshotAsync(
    DeviceInfo device,
    CancellationToken ct = default);
```

`CameraSnapshot` contains pre-session but handle-gated information such as supported
formats and control metadata. It is not an enricher and does not modify `DeviceInfo`.

### Decision 5 — The first-class capture abstraction is `CameraSession`

The preferred application-facing capture primitive is not the low-level device handle.
It is a configured, frame-producing session.

`CameraDevice` remains valuable as the low-level opened endpoint for advanced callers
that want direct access to capability queries and control surfaces. But the primary
abstraction for actual capture is `CameraSession`.

This split mirrors the rest of Periphery's architecture:

- `CameraDevice` owns the opened camera endpoint and low-level control operations
- `CameraSession` owns negotiated configuration, frame production, buffer pooling, and
  capture runtime semantics

For ergonomics, both of these construction paths are valid:

- `CameraSession.OpenAsync(DeviceInfo, ...)` — the common path for application code
- `CameraDevice.OpenAsync(...); camera.OpenSessionAsync(...)` — the advanced path when
  the caller wants explicit low-level device control before or alongside capture

### Decision 5a — Session/device ownership is explicit

The ownership rule for `CameraSession` depends on how the session was created:

- **Convenience path:** `CameraSession.OpenAsync(DeviceInfo, ...)` creates and owns the
  underlying `CameraDevice`. Disposing the session also disposes that device.
- **Advanced path:** `CameraDevice.OpenAsync(...); device.OpenSessionAsync(...)` leaves
  `CameraDevice` ownership with the caller. Disposing the session does not dispose the
  device.

`CameraSession.Device` is valid only for the lifetime of the session. After the session
is disposed, the reference may still exist, but using it is invalid.

### Decision 5b — `CameraSession` is single-capture in v1

`CameraSession` represents one configured capture runtime. Overlapping capture operations
on the same session are not supported in v1.

Invalid combinations include:

- calling `CaptureAsync(...)` while another `CaptureAsync(...)` enumeration is active
- calling `StartCaptureAsync(...)` while `CaptureAsync(...)` is active
- calling `CaptureAsync(...)` while `StartCaptureAsync(...)` has already started an
  active capture loop

These fail fast with a camera-specific exception rather than silently multiplexing or
starting a second capture path.

### Decision 5c — `CameraSession` is a camera-specific runtime layer

ADR-0024's Layer 1 / Layer 2 / Layer 3 pattern remains the architectural baseline, but
camera capture introduces a distinct intermediate concept: the configured capture
runtime. `CameraSession` is not a reconnect lifecycle manager. It is a camera-specific
runtime abstraction that sits between the low-level device primitive and the lifecycle
host.

To avoid implying a false 1:1 mapping with ADR-0024's numbering, the API sketch in this
ADR uses the terms **primitive**, **runtime**, and **host** instead of Layer 1 / Layer 2
 / Layer 3.

### Decision 6 — Reconnect-resilient lifecycle publishes `CameraSession`

The reconnect-resilient application-facing lifecycle should publish sessions, not raw
camera handles.

The recommended shape follows ADR-0032: `DeviceSessionHost<CameraSession>` is the
preferred long-lived host abstraction for camera applications. A specialized
`CameraSessionHost` convenience type may be added later, but it should be a thin wrapper
over `DeviceSessionHost<CameraSession>`, not a separate lifecycle model.

This aligns the core camera package with the rest of Periphery's session publication
patterns and gives higher-level layers a stable source abstraction even while physical
camera connections churn underneath.

The lifecycle rule from the patterns guide applies unchanged here:

- Periphery owns camera discovery, open/close, and reconnect windows
- `DeviceSessionHost<CameraSession>` owns session publication and withdrawal
- application/session supervision owns health interpretation and policy above the session

That means the core camera package should not embed a second reconnect state machine in
capture supervision, preview helpers, or future pipeline adapters.

### Decision 6a — Camera-specific convenience hosts are wrappers, not a new lifecycle model

If the package later adds a `CameraSessionHost` convenience type, it should be a thin
wrapper over `DeviceSessionHost<CameraSession>`.

It must not introduce a parallel camera-only lifecycle abstraction with different
reconnect or publication semantics. The generic session host is already the established
Periphery lifecycle boundary.

### Decision 6b — Session readiness and ongoing supervision are separate concerns

The patterns guidance around readiness versus liveness applies directly to camera I/O.

Examples of **session readiness** concerns that belong in camera/session creation:

- validating that the device can be opened
- validating that the requested format/configuration can be negotiated
- performing any one-shot startup checks required before capture is considered usable

Examples of **ongoing supervision** that belong above the session:

- FPS monitoring
- dropped-frame alarms
- encoder/inference health tracking
- preview or stream quality policy
- application-specific decisions about degraded mode vs failure

The session should expose the facts needed for supervision, but the supervision policy
itself belongs above the session boundary.

### Decision 7 — Public API favors raw frames, not UI or codec types

`Periphery.Camera` exposes neutral frame abstractions rather than UI types
(`BitmapSource`, `CGImage`, `SoftwareBitmap`) or codec-specific types (`AVFrame`).
This keeps the base package usable in services, CLIs, NativeAOT binaries, desktop
apps, and server processes without pulling in UI or FFmpeg dependencies.

### Decision 8 — Frame ownership is explicit and stable

Frame delivery must have an explicit, low-allocation ownership model.

The contract is:

- native camera buffers are returned to the OS/backend as quickly as possible
- consumers do **not** lease driver-owned buffers directly
- `Periphery.Camera` delivers frames from library-owned buffers
- the default delivery mode is a **leased, pooled frame** for high-throughput scenarios
- consumers that need retention create an **owned copy** explicitly

An active lease is stable until the consumer disposes it. The library must never:

- revoke an active lease
- silently relocate or replace the backing memory of a live frame
- mutate or reuse a leased buffer before disposal

This explicitly rejects the idea of automatically copying a live leased frame under
pressure and rewriting its internals to point at new heap memory. That behavior is too
implicit, breaks the meaning of a lease, and does not compose safely with aliases,
plane views, or downstream native interop.

### Decision 8b — Frame ownership is ref-counted, not single-owner

**Amendment (2026-05-09).** Decision 8's contract — pooled buffers, no
revocation, no mutation under live readers — stays. The single-owner
shape under that contract changes: <see cref="ICameraFrame"/> is now
**ref-counted** rather than single-owner-lease.

The public surface gains one method:

```csharp
public interface ICameraFrame : IDisposable
{
    // existing members …
    ICameraFrame AddRef();
}
```

Semantics:

- A frame produced by the pool starts at refcount = 1 (the initial reference).
- `AddRef()` atomically increments the count and returns the same instance.
  Calling `AddRef()` after the final `Dispose()` (i.e. the buffer has already
  returned to the pool) throws `ObjectDisposedException` — that's a use-after-
  release bug, surfaced loudly.
- `Dispose()` atomically decrements; the buffer returns to the pool only when
  the count reaches zero. Each `AddRef()` requires a balancing `Dispose()`.
- Double-`Dispose()` of the initial reference (or any other sequence that
  drops the count below zero) is undefined behavior — fails fast under
  `DEBUG`, no-ops under `RELEASE` to preserve pool integrity.
- `OwnedCameraFrame` adopts the same shape for interface uniformity, even
  though its byte buffer is GC-managed rather than pool-managed (refcount
  zero is a no-op there; the GC reclaims when no managed roots remain).

#### Reasoning for the switch

1. **Multicast is the default access pattern for modern applications.** Live
   preview + record + inference is increasingly the *typical* topology, not
   the exception. Single-owner lease made the simple case simple at the cost
   of paying a copy at every fan-out point. Ref-counting makes zero-copy
   fan-out the default and reserves explicit `Copy()` for "I want bytes that
   are independent of the pool entirely."

2. **Ecosystem convergence.** FrameFlow uses ref-counting natively (inherited
   from FFmpeg's `av_frame_ref` / `av_frame_unref` model). The graph runtime
   of the day treated both ownership models uniformly via an
   `IFrame : IDisposable` base, but a refcount-on-both-sides pattern lets the
   future `FrameFlow.Camera` bridge package pass frames through without
   promoting lease → refcount at the boundary. The bridge becomes thinner.

3. **`OutstandingLeases` semantics survive.** The pool's outstanding-buffer
   count tracks *buffers in flight*, not *references in flight*. A frame
   with three live references still consumes one pool slot; it returns when
   all three references dispose. `CameraSessionMetrics.OutstandingLeases`
   continues to mean "buffers checked out from the pool," which is what
   supervision policy actually reasons about.

#### What stays unchanged

- The pool, exhaustion policies, and capacity contracts (Decision 9) are
  unaffected — they reason about buffers, not references.
- `Copy()` → `OwnedCameraFrame` remains valuable for "escape the pool
  entirely with an independent allocation." It's a different escape valve
  from `AddRef()` (zero-copy retention while the pool buffer is still
  in flight) and both keep their place.
- Existing consumers using `using (var frame = …) { … }` are correct without
  modification: the initial refcount of 1 is dropped to 0 by the single
  `Dispose`, which is exactly the previous lease behavior. Only consumers
  that want to retain or share frames need to learn `AddRef()`.
- The "library must never revoke / relocate / mutate live frames" guarantee
  in Decision 8 is unchanged — refcounting is purely about *who decides when
  the lifetime ends*, not about the buffer contract during the lifetime.

### Decision 9 — Pool exhaustion affects future frames, not active leases

If consumers hold leased frames longer than the pool budget allows, the consequence is
applied to **future** frames only. Active leases remain valid.

The capture pipeline therefore has a configurable exhaustion policy:

- `BlockProducer` — wait for a lease to be returned
- `DropIncoming` — discard newly arrived frames when no delivery buffer is available
- `DropOldestQueued` — if an internal queue exists, evict older queued frames first
- `AllocateOverflow` — allocate an owned overflow frame outside the pool

The default policy is `DropIncoming`, because it preserves bounded memory and low
latency for real-time preview, streaming, and inference workloads.

> **Amended by [ADR-0082](0082-a-camera-session-is-lossy.md).** The four values above
> shipped, but all four behaved identically — the code that distinguished them could not
> be reached. `BufferExhaustionPolicy` now has two: `LatestWins` (the default, formerly
> `DropOldestQueued`) and `StallProducer` (formerly `BlockProducer`). `DropIncoming` and
> `AllocateOverflow` were deleted, not implemented. The rest of this decision — active
> leases are never revoked, and the knobs are session-scoped — stands.

Pool and queue configuration are session-scoped concerns. Buffer count, queue depth, and
exhaustion policy are therefore configured on `CameraSessionOptions`, not on individual
capture method calls.

### Decision 10 — Per-frame disposal is synchronous

Returning a leased frame to the pool is a synchronous operation. The per-frame lease type
therefore uses `IDisposable`, not `IAsyncDisposable`.

`CameraDevice`, `CameraSession`, and `DeviceSessionHost<CameraSession>` remain
asynchronous disposables because shutting down the underlying capture pipeline may
involve backend I/O and worker-task teardown.

### Decision 10a — Mid-capture device loss faults the active capture operation

If the device disappears or the backend fails during an active capture, the active
capture operation faults with `CameraDeviceLostException` (derived from
`CameraException`). It does not complete silently.

This applies to:

- an active `CaptureAsync(...)` enumeration
- an in-flight `ReadFrameAsync(...)`
- any start/read path that is actively waiting on backend frame delivery

`CameraDeviceLostException` is preferred over a name such as
`CameraDisconnectedException` because "disconnected" is ambiguous between an expected
shutdown path and unexpected physical loss. The exception should carry the `DeviceInfo`
for the lost device so reconnect orchestration and diagnostics can make decisions
without separately recovering identity.

Normal caller-requested cancellation continues to use the supplied
`CancellationToken`/`OperationCanceledException` semantics. Intentional stop/dispose
paths therefore remain distinct from unexpected device-loss faults.

### Decision 11 — Plane-aware layout is first-class

The base package must represent frame layout explicitly enough for displays, encoders,
streamers, and inference engines to consume frames without mandatory repacking.

That requires more than a single untyped byte buffer. The frame contract must expose:

- dimensions
- pixel format
- timestamp
- plane count
- per-plane stride and extents
- contiguous packed-buffer access when the format actually is contiguous

Formats such as NV12, I420, YUY2, and MJPEG must not be flattened into an ambiguous blob
that forces every downstream consumer to reverse-engineer layout details.

**Refined by [ADR-0081](0081-a-delivered-frame-has-tight-rows.md), implemented for
[#320](https://github.com/charles8051/periphery/issues/320).** Per-plane stride and
extents stay first-class, and so does the ban on flattening. What changed is the claim
that the stride must *vary* to avoid a copy: every frame is already copied into the pool
unconditionally, so there is no copy to avoid, and a stride that varies by driver,
resolution and platform is itself the layout detail consumers were reverse-engineering.
`CameraPlane.Stride` is now an invariant — `CameraFrameLayout.BytesPerRow(format,
planeWidth)` for every plane of every uncompressed frame — asserted in the pool. The
repacking D11 called out is mandatory, and ADR-0081 argues it is the cheaper contract.

---

## Package Layout Sketch

The initial package graph is a strict star rooted on `Periphery` core:

```text
Periphery
├── Periphery.Camera
├── Periphery.Camera.Windows        (optional split if TFM or WinRT coupling appears)
├── Periphery.Camera.Linux          (optional split if native bindings warrant it)
├── Periphery.Camera.MacOS          (optional split if Apple interop warrants it)
└── Periphery.Camera.FFmpeg         (optional future adapter; not required for camera I/O)
```

The initial implementation should start as a single package unless a backend forces a
package split for TFM or native interop reasons.

### Suggested source layout

```text
src/
  Periphery.Camera/
    Periphery.Camera.csproj
    CameraDevice.cs
    CameraSession.cs
    CameraException.cs
    CameraDeviceLostException.cs
    ICameraFrame.cs
    CameraSnapshot.cs
    LeasedCameraFrame.cs
    OwnedCameraFrame.cs
    CameraPlane.cs
    CameraFormat.cs
    CameraCaptureOptions.cs
    CameraConfiguration.cs
    CameraSessionOptions.cs
    CameraOpenOptions.cs
    CameraControlInfo.cs
    CameraControlKind.cs
    BufferExhaustionPolicy.cs
    CameraPixelFormat.cs
    Rational.cs
    CameraTransport.cs
    Internal/
      ICameraBackend.cs
      ICameraSnapshotBackend.cs
      CameraEndpointResolver.cs
      CameraFrameBufferLease.cs
      CameraFramePool.cs
      CameraOwnedFrameFactory.cs
    Windows/
      MediaFoundationCameraBackend.cs
      MediaFoundationCameraSnapshotBackend.cs
      MediaFoundationCameraResolver.cs
    Linux/
      V4l2CameraBackend.cs
      V4l2CameraSnapshotBackend.cs
      V4l2CameraResolver.cs
    MacOS/
      AvFoundationCameraBackend.cs
      AvFoundationCameraSnapshotBackend.cs
      AvFoundationCameraResolver.cs

  Periphery.Camera.FFmpeg/           (future, optional)
    Periphery.Camera.FFmpeg.csproj
    CameraFrameFfmpegExtensions.cs
    FfmpegCameraSink.cs
    FfmpegPixelConverter.cs
```

### Package boundaries

`Periphery.Camera` owns:

- opening and closing the native camera device
- format enumeration and selection
- frame capture
- camera controls
- native endpoint correlation with `DeviceInfo`
- reconnect lifecycle helpers

`Periphery.Camera.FFmpeg` would own only:

- encoding / decoding
- remuxing
- network sink / source adapters
- pixel-format conversion where FFmpeg materially improves support

It must not become a backdoor alternate device-open path that bypasses `CameraDevice`.

---

## Public API Sketch

### Primitive — `CameraDevice`

```csharp
public sealed class CameraDevice : IAsyncDisposable
{
    public DeviceInfo DeviceInfo { get; }

    // Backend-native endpoint identifier for diagnostics only.
    public string NativeEndpointId { get; }

    public static Task<CameraDevice> OpenAsync(
        DeviceInfo device,
    CameraOpenOptions? options = null,
        CancellationToken ct = default);

    public static Task<CameraSnapshot> ReadSnapshotAsync(
        DeviceInfo device,
        CancellationToken ct = default);

    public Task<CameraSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(
        CancellationToken ct = default);

    public Task<CameraSession> OpenSessionAsync(
      CameraConfiguration configuration,
      CameraSessionOptions? options = null,
        CancellationToken ct = default);

    public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(
      CancellationToken ct = default);

    public Task SetControlAsync(
      CameraControlKind control,
      double value,
      CancellationToken ct = default);

    public Task ResetControlAsync(
      CameraControlKind control,
      CancellationToken ct = default);
  }
  ```

  **Notes:**

  - `OpenAsync(DeviceInfo, ...)` is the low-level opened-endpoint construction path.
  - `ReadSnapshotAsync` follows ADR-0026: explicit handle-gated metadata read.
  - `CameraDevice` owns device-level capability inspection and controls.
  - `OpenSessionAsync(...)` creates the configured frame-producing runtime object.
  - `CameraDevice` is the advanced primitive, not the preferred long-lived application
    boundary.
  - Control APIs live on `CameraDevice` first. `CameraSession` may expose convenience
    forwarding methods later if needed, but the device is the authoritative owner of
    camera controls in the initial design.
  - Control mutation during active capture is backend-dependent and best-effort.
    Unsupported mutations during capture should fail with a camera-specific exception
    rather than silently succeeding.

  ### Runtime — `CameraSession`

  ```csharp
  public sealed class CameraSession : IAsyncDisposable
  {
    public CameraDevice Device { get; }
    public DeviceInfo DeviceInfo { get; }
    public CameraConfiguration Configuration { get; }
    public CameraSessionOptions Options { get; }
    public bool IsCapturing { get; }
    public CameraSessionMetrics Metrics { get; }

    public static Task<CameraSession> OpenAsync(
      DeviceInfo device,
      CameraConfiguration configuration,
      CameraOpenOptions? deviceOptions = null,
      CameraSessionOptions? sessionOptions = null,
      CancellationToken ct = default);

    public IAsyncEnumerable<LeasedCameraFrame> CaptureAsync(
      CancellationToken ct = default);

    public Task StartCaptureAsync(CancellationToken ct = default);

    public Task<LeasedCameraFrame> ReadFrameAsync(
      CameraCaptureOptions? options = null,
      CancellationToken ct = default);

    public Task StopCaptureAsync(CancellationToken ct = default);
}
```

**Notes:**

  - `CameraSession.OpenAsync(DeviceInfo, ...)` is the preferred application-facing capture
    entry point.
- `CameraDevice.OpenAsync(...); device.OpenSessionAsync(...)` remains the advanced path
  for callers that want explicit low-level control before or alongside capture.
  - `CaptureAsync` is the canonical high-level streaming surface.
  - `StartCaptureAsync` + `ReadFrameAsync` + `StopCaptureAsync` are the lower-level pull
  model for consumers that need explicit loop ownership.
- Leased frames are disposed by the consumer to return the buffer to the session pool.
- Consumers that need to retain frame data call `Copy()` / `ToOwnedFrame()` and release
  the lease promptly.
- `CameraSession` owns active configuration, frame production, buffer pooling, and
  capture-runtime semantics.
- `CameraSession` should be the main source abstraction consumed by future pipeline
  layers.
- Opening a session does not necessarily begin frame production immediately. Capture
  begins when `CaptureAsync(...)` is enumerated or when `StartCaptureAsync(...)` is
  invoked explicitly.

  ### Host — `DeviceSessionHost<CameraSession>`

```csharp
  public sealed class DeviceSessionHost<CameraSession> : IAsyncDisposable
  {
    public static Task<DeviceSessionHost<CameraSession>> StartAsync(
        DeviceProfile profile,
      Func<DeviceInfo, CancellationToken, Task<CameraSession>> createSession,
      Func<CameraSession, Task>? onSessionEnded = null,
      Func<CameraSession, CancellationToken, Task>? whileSessionActive = null,
        CancellationToken ct = default);

    public static DeviceSessionHost<CameraSession> Create(
        DeviceTracker tracker,
      Func<DeviceInfo, CancellationToken, Task<CameraSession>> createSession,
      Func<CameraSession, Task>? onSessionEnded = null,
      Func<CameraSession, CancellationToken, Task>? whileSessionActive = null);
}
```

  This is the preferred reconnect-resilient API for long-lived camera applications.

  **Notes:**

  - The host owns session publication and withdrawal, not camera policy.
  - Health supervision above the session may decide that a session is unhealthy enough to
    fail, but reconnect still belongs to the lifecycle owner.
  - A future `CameraSessionHost` convenience type should delegate to this shape, not
    replace it.

### Core value types

```csharp
public class CameraException : Exception
{
  public DeviceInfo? DeviceInfo { get; }
}

public sealed class CameraDeviceLostException : CameraException
{
  public CameraDeviceLostException(
    DeviceInfo deviceInfo,
    string? message = null,
    Exception? innerException = null);
}

public sealed record CameraSnapshot(
    string NativeEndpointId,
    IReadOnlyList<CameraFormat> Formats,
    IReadOnlyList<CameraControlInfo> Controls);

public sealed record CameraFormat(
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    Rational MinFrameRate,
    Rational MaxFrameRate,
    CameraTransport Transport);

public sealed record CameraConfiguration(
    CameraFormat Format,
    Rational? TargetFrameRate = null,
    bool DropLateFrames = true);

public sealed record CameraSessionOptions(
  int BufferCount = 3,
  BufferExhaustionPolicy ExhaustionPolicy = BufferExhaustionPolicy.DropIncoming,
  int QueueDepth = 1);

public sealed record CameraSessionMetrics(
    long FramesProduced,
    long FramesDropped,
    int OutstandingLeases,
    TimeSpan? LastFrameTimestamp);

public sealed record CameraOpenOptions(
  TimeSpan? OpenTimeout = null);

public sealed record CameraCaptureOptions(
  TimeSpan? FrameTimeout = null);

public readonly record struct Rational(
  int Numerator,
  int Denominator);

public sealed record CameraControlInfo(
    CameraControlKind Kind,
    string Name,
    double? MinValue,
    double? MaxValue,
    double? Step,
    double? DefaultValue,
    bool SupportsAutoMode,
    bool IsReadOnly);

  public enum CameraPixelFormat
  {
    Mjpeg,
    Nv12,
    Yuy2,
    I420,
    Rgb24,
    Bgr24,
    Bgra32,
  }

  public enum CameraTransport
  {
    UsbUvc,
    Integrated,
    Virtual,
    Network,
    Other,
  }

  public enum CameraControlKind
  {
    Exposure,
    Gain,
    Brightness,
    Contrast,
    Saturation,
    Sharpness,
    WhiteBalance,
    Focus,
    Zoom,
    Torch,
  }

  public enum BufferExhaustionPolicy
{
    BlockProducer,
    DropIncoming,
    DropOldestQueued,
    AllocateOverflow,
}

  public readonly record struct CameraPlane(
    ReadOnlyMemory<byte> Buffer,
    int Stride,
    int Width,
    int Height);

  public interface ICameraFrame : IDisposable
  {
    int Width { get; }
    int Height { get; }
    CameraPixelFormat PixelFormat { get; }
    TimeSpan Timestamp { get; }
    int PlaneCount { get; }
    bool IsContiguous { get; }
    ReadOnlyMemory<byte> ContiguousBuffer { get; }
    CameraPlane GetPlane(int index);
  }

  public sealed class LeasedCameraFrame : ICameraFrame
  {
    public int Width { get; }
    public int Height { get; }
    public CameraPixelFormat PixelFormat { get; }
    public TimeSpan Timestamp { get; }
    public int PlaneCount { get; }
    public bool IsContiguous { get; }
    public ReadOnlyMemory<byte> ContiguousBuffer { get; }
    public CameraPlane GetPlane(int index);

    public OwnedCameraFrame Copy();
  }

  public sealed class OwnedCameraFrame : ICameraFrame
  {
    public int Width { get; }
    public int Height { get; }
    public CameraPixelFormat PixelFormat { get; }
    public TimeSpan Timestamp { get; }
    public int PlaneCount { get; }
    public bool IsContiguous { get; }
    public ReadOnlyMemory<byte> ContiguousBuffer { get; }
    public CameraPlane GetPlane(int index);
  }
```

`ICameraFrame` is intentionally a minimal, read-only frame surface. Ownership-
transition operations such as `Copy()` stay on concrete frame types like
`LeasedCameraFrame`, not on the shared interface.

  The load-bearing requirement is that the base package exposes raw frame data with an
  explicit ownership model and without taking a dependency on UI image types or
  FFmpeg-specific frame structs.

### Golden core shape

The intended steady-state API shape is:

- `CameraDevice` is the low-level opened endpoint for identity, snapshotting, capability
  discovery, and controls.
- `CameraSession` is the preferred application-facing capture source.
- `DeviceSessionHost<CameraSession>` is the reconnect-resilient publication boundary for
  long-lived applications.

The design should optimize for three caller profiles simultaneously:

1. simple application code that wants a configured frame source quickly
2. advanced callers that want explicit low-level device control
3. long-lived applications that need reconnect-resilient session publication

That implies two equally valid construction paths:

- common path: `CameraSession.OpenAsync(DeviceInfo, CameraConfiguration, ...)`
- advanced path: `CameraDevice.OpenAsync(...); device.OpenSessionAsync(...)`

The API should preserve both. The former is the preferred application shape; the latter
is the escape hatch for advanced scenarios.

The session object should remain focused on runtime capture semantics:

- negotiated configuration
- frame production
- buffer pooling and lease accounting
- session-level metrics needed by higher-level supervision

The device object should remain focused on device-level concerns:

- native identity
- capability snapshotting
- control discovery and mutation
- creation of sessions

This split keeps the core package small, honest, and compatible with the higher-level
pipeline layer described in ADR-0036.

### Usage sketch

```csharp
var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithName("Logitech")
    .FirstAsync(ct);

var snapshot = await CameraDevice.ReadSnapshotAsync(device, ct);
var format = snapshot.Formats
    .Where(f => f.Width == 1920 && f.Height == 1080)
    .OrderByDescending(f => f.MaxFrameRate)
    .First();

await using var session = await CameraSession.OpenAsync(
    device,
    new CameraConfiguration(format),
    ct: ct);

await foreach (var frame in session.CaptureAsync(ct: ct))
{
  using (frame)
  {
    Process(frame.ContiguousBuffer.Span, frame.Width, frame.Height, frame.PixelFormat);
  }
}
```

---

## Non-Goals

The first version of `Periphery.Camera` does **not** attempt to solve all video/media
problems.

Out of scope for the initial package:

- video file decode / playback
- video encode / mux / demux
- RTSP / RTMP / WebRTC transport
- image-processing algorithms
- GPU effects pipelines
- multi-camera synchronization guarantees
- vendor SDK wrappers
- cross-platform UI preview controls
- application-specific session supervision or health policy
- a camera-specific lifecycle abstraction separate from `DeviceSessionHost<CameraSession>`

These may be layered on top later, but they are not the purpose of the camera I/O
foundation.

---

## Consequences

### Positive

- Device identity stays anchored in the authoritative OS camera APIs.
- The package fits the existing Periphery extension architecture cleanly.
- Camera capabilities remain explicit and do not leak hidden I/O into enumeration.
- FFmpeg can still be used later where it is strongest, without distorting the core
  package shape.
- The camera package now aligns with the existing session-host and supervision patterns
  already documented elsewhere in the repo.

### Negative

- Three distinct backend implementations are required.
- macOS open-time permission prompts and app-bundle requirements become an explicit part
  of the package contract.
- V4L2 is a lower-level API than Media Foundation or AVFoundation; the Linux backend
  will require more direct buffer and ioctl management.

### Neutral / follow-up questions

- Whether Linux should remain V4L2-only initially or gain a later `libcamera` backend.
- Which exact multi-planar and compressed pixel formats should be treated as first-class
  in v1 beyond the initial baseline set sketched above.
- Whether `Periphery.Camera.Windows` should split out if Media Foundation or WinRT
  interop introduces target-framework coupling similar to ADR-0018.
