# Plan: Avalonia camera preview from `Periphery.Camera`

> **Status:** delivered, and then both deferred parts happened anyway. The preview runs
> as [`examples/Periphery.Examples.CameraAvalonia`](../../examples/Periphery.Examples.CameraAvalonia),
> the packaged UI control this plan deliberately deferred exists as
> [`src/Periphery.Camera.Avalonia`](../../src/Periphery.Camera.Avalonia) (`CameraPreview`),
> and stage 3's pixel formats shipped in
> [#318](https://github.com/charles8051/periphery/issues/318). Pipelines did not:
> `Periphery.Camera.Pipelines` was never built
> ([ADR-0045](../adr/0045-substrate-independence-from-crossbar.md)), which is why
> stage 3 as written below is superseded — see the note under it.

## Goal

Render a live camera feed in an Avalonia window using `Periphery.Camera`,
without committing to higher-order infrastructure (pipelines, pixel
converters, a packaged UI control) until we know we need it.

## Direct answers to the design questions

| Question | Answer |
|---|---|
| Build `Periphery.Camera.Pipelines` (ADR-0036) for this? | **No.** Pipelines is a graph runtime — branching, encoding, cross-operator backpressure. A live preview is a single linear consumer; a graph runtime is a sledgehammer. Defer until you actually want simultaneous preview + record + inference. |
| Expand the core sinks in `Periphery.Camera`? | **No.** ADR-0040 §3 deliberately scopes core sinks to byte-level work. JPEG decode and YUV→BGRA conversion are pixel-interpretation; they belong above core in a separate package. Adding them to core would erase a load-bearing line. |
| Need either to build the Avalonia component? | **No.** What we actually need: per-frame conversion to `Bitmap`/`WriteableBitmap`, UI-thread marshalling, view-lifecycle integration, and reconnect resilience. None of that depends on pipelines or new core sinks. |

## Three stages — only stage 1 is committed

### Stage 1 — MVP example app *(this plan)*

`examples/Periphery.Camera.Avalonia.Example/` — a thin Avalonia app
that opens the camera through the existing `Periphery.Camera` builder,
restricts to **MJPEG only**, and decodes each frame straight into an
Avalonia `Bitmap` for display.

Why MJPEG-only:

- Every USB UVC camera advertises MJPEG.
- Avalonia/Skia decodes JPEG natively — no new dependency.
- One line of pixel-handling code: `new Bitmap(memoryStream)`.
- For preview at ≤1080p30, JPEG decode CPU is comfortably sub-millisecond
  on any modern desktop — perf is not a real concern.

Cameras that don't expose MJPEG fail open with
`CameraConfigurationException`. That's the correct UX for v1: a clear
error beats a half-working preview.

Reconnect resilience comes for free via `DeviceSessionHost<CameraSession>`
(ADR-0032 / ADR-0035 §6) — the existing `HostCommand` in the CLI
example demonstrates the pattern.

UI shape (deliberately minimal):

- Top: `ComboBox` of cameras + Refresh button.
- Middle: `Image` filling the window.
- Bottom: status line driven by `DeviceSessionHost.Status` transitions.

Done = pick a webcam from the dropdown, see live preview, replug the
camera and watch the preview resume.

### Stage 2 — extract `Periphery.Camera.Avalonia` *(only after stage 1 ships)*

Once stage 1 settles, lift the reusable mechanics into a published
package:

- `CameraPreviewControl : Control` — Avalonia control that owns the
  session lifecycle and exposes UI properties (`DeviceFilter`,
  `MaxResolution`, `IsPreviewActive`, status binding).
- `WriteableBitmap` reuse so the preview path doesn't allocate per
  frame in steady state.
- MVVM-friendly bindings (`INotifyPropertyChanged` on status).
- Still MJPEG-only at this stage. Library stays small and
  single-purpose; non-UI consumers don't pull Avalonia in.

### Stage 3 — broader pixel format support *(delivered, and not as written)*

> **Superseded by [#318](https://github.com/charles8051/periphery/issues/318).**
> The paragraphs below are kept for the record. Three of their premises did not
> survive: there is no `OnFormatChangedAsync` caller to key a surface cache off,
> because the pipeline runtime was never built (ADR-0045); a second UI framework
> never appeared, so `Periphery.Camera.Imaging` was rejected as over-scoped for
> two converters; and the conversion is scalar, because nothing has measured the
> scalar loop as too slow at the resolutions `MaxResolution` caps a preview to. What shipped is in
> [`src/Periphery.Camera.Avalonia/README.md`](../../src/Periphery.Camera.Avalonia/README.md#formats).

**As delivered.** The control negotiates rather than filters: it opens on the
best format the camera advertises that it can display, preferring `Bgra32` and
`Rgba32` (a strided row copy into a natively-created `WriteableBitmap`), then
`Mjpeg` (Skia decode), then `Nv12` and `Yuy2` (scalar BT.601 conversion, ~70
lines, in `PreviewPixels`). Resolution and frame rate outrank the format
preference. Anything else fails at `OpenAsync` with a message naming what the
camera offered. Surfaces are reused across frames, keyed on width, height and
Avalonia pixel format, and the control calls its own `OnFormatChangedAsync`
from its capture loop since nothing else will.

**As originally written, for the record:**

If a target camera you care about doesn't expose MJPEG, or JPEG decode
CPU becomes painful at 4K60, add NV12 → BGRA32 conversion. Pure C#
with `Vector<T>` should hit ~3–4 GB/s — plenty for 1080p60. Lives in
`Periphery.Camera.Avalonia`, or extracted to `Periphery.Camera.Imaging`
if a second UI framework (WPF, MAUI) appears.

The plane-aware delivery work that just landed in `MfCameraBackend`
(b887a9e) makes this straightforward — `frame.GetPlane(0)` is Y,
`frame.GetPlane(1)` is UV with the right strides; the missing piece
is the conversion routine itself.

## What this plan is *not* doing

- **Building pipelines.** Wait for a concrete graph need (preview + record
  + inference) before designing a graph runtime.
- **Expanding core sinks.** Pixel-aware operations are above core by ADR.
- **Skipping straight to a polished package.** Stage 2 without stage 1
  tends to over-design; the example forces the actual shape first.
- **Solving every camera.** MJPEG-only was fine for v1; the YUV-only case
  turned up and stage 3 addressed it for YUY2 and NV12. `Uyvy`, `I420`,
  `Yv12` and `Nv21` still fail at open.

## Cross-references

- [ADR-0035 — Periphery.Camera](../adr/0035-periphery-camera.md) — the
  foundation this plan builds on.
- [ADR-0036 — Periphery.Camera.Pipelines](../adr/0036-periphery-camera-pipelines.md)
  — explicitly deferred.
- [ADR-0040 — Camera ergonomic roadmap](../adr/0040-camera-ergonomic-roadmap.md)
  — establishes the no-pixel-interpretation-in-core rule that scopes
  this work to a separate package.
- [`docs/surface/examples_generic-session-host-example.md`](../surface/examples_generic-session-host-example.md)
  — the host pattern this app follows.
