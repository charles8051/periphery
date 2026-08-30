# Periphery.Camera.Avalonia

Avalonia controls for [`Periphery.Camera`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera). Drop
`<CameraPreview>` into a window, bind it to a `DeviceInfo`, and a live
camera feed appears — no capture loop, no UI-thread plumbing, no
session-host wiring.

## Install

Add the package alongside `Periphery.Camera`:

```bash
dotnet add package Periphery.Camera.Avalonia --prerelease
```

## Use it

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:cam="https://periphery.dev/camera-avalonia">

  <Grid RowDefinitions="Auto,*,Auto">

    <!-- Pick a camera (your existing UI; control is unopinionated about how) -->
    <ComboBox x:Name="DevicePicker" Grid.Row="0" .../>

    <!-- Live preview. The Device binding is the entire wiring. -->
    <cam:CameraPreview Grid.Row="1"
                       Name="Preview"
                       Device="{Binding ElementName=DevicePicker, Path=SelectedItem}"
                       MaxResolution="1280,720"/>

    <!-- Status text: bind to the control's StatusDescription DP. -->
    <TextBlock Grid.Row="2"
               Text="{Binding ElementName=Preview, Path=StatusDescription}"/>

  </Grid>
</Window>
```

That's the whole integration. The control owns:

- `DeviceSessionHost<CameraSession>` — opens the session, runs the
  capture loop, handles unplug/replug reconnect.
- Format negotiation, pixel conversion or JPEG decode, and UI-thread
  marshalling.
- Disposal when the control leaves the visual tree.

## Public surface

| Member | Kind | Default | Purpose |
|---|---|---|---|
| `Device` | DP, read/write | `null` | The camera to preview. Setting null disconnects. |
| `MaxResolution` | DP, read/write | `1280×720` | Max resolution to negotiate at session open. |
| `IsLive` | DP, read-only | `false` | `true` when a session is active and frames are flowing. |
| `StatusDescription` | DP, read-only | `"Idle."` | UI-friendly status string. Bind to a TextBlock. |
| `LastError` | DP, read-only | `null` | Most recent open-time / capture-loop error. Cleared on successful reconnect. |

All five are `AvaloniaProperty` registrations, so MVVM bindings work
without manual `INotifyPropertyChanged` plumbing.

## Formats

The control opens the camera on the best format it can display, out of
what the camera advertises. There is no `AllowOnlyPixelFormats` filter to
configure: the policy is fixed and it is this table.

| Camera format | How it reaches the screen | Cost per frame |
|---|---|---|
| `Bgra32` | strided row copy into a `Bgra8888` `WriteableBitmap` | one memcpy per row |
| `Rgba32` | strided row copy into an `Rgba8888` `WriteableBitmap` | one memcpy per row |
| `Mjpeg` | Skia JPEG decode into a fresh `Bitmap` | a decode and one allocation |
| `Nv12` | scalar BT.601 conversion into a `Bgra8888` `WriteableBitmap` | a managed per-pixel loop |
| `Yuy2` | scalar BT.601 conversion into a `Bgra8888` `WriteableBitmap` | a managed per-pixel loop |

Anything else — `Uyvy`, `I420`, `Yv12`, `Nv21`, `Bgr24`, `Rgb24`,
`Argb32`, `Gray8`, `Gray16` — fails at `OpenAsync`, and `LastError`
carries a message naming the camera's formats and the five above.

Selection order is **area, then frame rate, then the table's order**.
Resolution and frame rate are what a viewer sees; the format preference
decides between the several formats a camera usually offers at the same
resolution and rate. A 1280×720 MJPEG stream at 30 fps therefore wins
over a 320×240 BGRA32 one, and loses to a 1280×720 BGRA32 one.

Colour is BT.601 limited range, which is what UVC cameras overwhelmingly
tag. `CameraFormat` carries no colorimetry, so there is nothing better to
key off; a BT.709 source comes out slightly oversaturated.

## Surfaces and memory

Raw formats write into a `WriteableBitmap` that is reused across frames,
keyed on width, height and Avalonia pixel format, and reallocated when any
of the three changes. Steady state is two surfaces — one being written,
one being displayed — so a 1280×720 preview holds about 7 MB of surface
and a 1080p one about 16.6 MB. `MaxResolution` defaults to 1280×720.

MJPEG still allocates a `Bitmap` per frame. Skia's decoder produces its own
output and there is nothing to write into.

## Scope

- **Windows and Linux**, per `Periphery.Camera`. On Linux, V4L2 exposes no
  32-bit RGB format at all, so the native-copy path is unreachable there and
  a camera arrives as MJPEG, YUY2 or NV12.
- **No `Stretch` / `StretchDirection`.** The preview is always uniform-fit
  within the control's bounds.

## Architecture

`CameraPreview` is a custom `Control` (no XAML, no inner `Image`)
that:

- **Implements `ICameraFrameSink`** from
  [`Periphery.Camera`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera) — the same shape as
  frame-flow's `IVideoSink`. The pipeline runtime that contract names as
  the caller was never built (ADR-0045), so the control drives its own
  capture loop and calls its own `OnFormatChangedAsync` before the first
  frame of each session.
- **Drops on overwrite via `Interlocked.Exchange`** — fast cameras
  don't pile up unrendered frames; the latest pending frame
  supersedes the previous one, which is disposed and counted via
  `DroppedFrameCount`.
- **Renders at a fixed ~60 Hz cadence** via a `DispatcherTimer`
  that invalidates the visual; the actual `Render(DrawingContext)`
  override pulls whatever frame is pending and draws it directly
  via `DrawImage`. This decouples render rate from camera frame
  rate so a 240 Hz industrial camera doesn't trigger 240 invalidations
  per second.

This is the same pattern frame-flow's `FrameFlowVideoView` /
`AvaloniaVideoSink` use, including the double-buffered `WriteableBitmap`
write path.

**Threading.** Three threads. The capture thread runs `PresentAsync`,
converts or decodes the frame, and publishes the surface. The UI thread
claims it in `Render` and records a draw command. The compositor replays
that command and performs the actual `DrawBitmap`, so the bitmap is read
after `Render` returns. Surfaces travel forward and back through
`Interlocked.Exchange` slots, and the capture thread never writes into
the current front surface. A just-retired one can still be referenced by
an unreplayed draw list; `WriteableBitmap.Lock()` and the compositor's
`DrawBitmap` take the same Skia monitor, so that write cannot tear,
though one side may wait for the other.

**The pure core.** Format policy (`PreviewPixelFormats`,
`PreviewFormatChoice`) and the pixel work (`PreviewPixels`) are total
functions over scalars and spans with no Avalonia types, per the
functional-core preference recorded in ADR-0052. That is also how they are
tested: Avalonia's default headless render interface accepts every pixel
format and hands `Lock()` a throwaway `Rgba8888` buffer at `width * 4`,
so a headless pixel test would pass for NV12 without converting
anything.

The control has no inner layout, so consumers can wrap it in their
own frame, overlay status text, etc.

## Cross-references

- [`docs/plans/periphery-camera-avalonia-preview.md`](https://github.com/charles8051/periphery/blob/main/docs/plans/periphery-camera-avalonia-preview.md)
  — the three-stage roadmap, all three of them now delivered.
- [`docs/adr/0081-a-delivered-frame-has-tight-rows.md`](https://github.com/charles8051/periphery/blob/main/docs/adr/0081-a-delivered-frame-has-tight-rows.md)
  — why the row copy can trust the frame's stride.
- [`Periphery.Camera`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera) — the underlying camera
  primitive (`CameraSession`, the fluent builder).
- [`Periphery`](https://github.com/charles8051/periphery/tree/main/src/Periphery) — `DeviceSessionHost<T>` and friends.
- [`docs/adr/0035-periphery-camera.md`](https://github.com/charles8051/periphery/blob/main/docs/adr/0035-periphery-camera.md)
  — ADR-0035 §1a explains the Windows-only v1 scope.
