# Periphery.Camera.Example

A small subcommand-based CLI that exercises the public surface of
[`Periphery.Camera`](../../src/Periphery.Camera). It pairs with the single-file
[`examples/scripts/camera.cs`](../scripts/camera.cs) script — the script shows
the simplest possible usage in a `dotnet run camera.cs` flow, while this
project shows the same patterns broken out into discrete, copy-pasteable
demos.

## Build

```bash
dotnet build examples/Periphery.Camera.Example/Periphery.Camera.Example.csproj
```

## Run

From the repository root:

```bash
dotnet run --project examples/Periphery.Camera.Example -- <command> [options]
```

Or after building:

```bash
./examples/Periphery.Camera.Example/bin/Debug/net10.0/periphery-camera-example <command>
```

## Commands

### `list`

Discovery only. No camera handles opened — pure
`Devices.Enumerate().OfCategory(DeviceCategory.Camera)`.

```bash
dotnet run --project examples/Periphery.Camera.Example -- list
```

### `snapshot [--device NAME]`

Brief open. Activates the camera stack just long enough to read formats and
controls, then closes. Backs the
[`CameraDevice.ReadSnapshotAsync`](../../src/Periphery.Camera/CameraDevice.cs)
helper from ADR-0026.

```bash
dotnet run --project examples/Periphery.Camera.Example -- snapshot
dotnet run --project examples/Periphery.Camera.Example -- snapshot --device "Logitech"
```

### `capture [--device NAME] [--frames N] [--save DIR] [--format mjpeg|nv12|yuy2]`

Streaming capture using `CameraSession.OpenAsync` + `CaptureAsync`. Picks the
highest-resolution format matching `--format` (default `mjpeg`), captures
`--frames` frames (default 30), and reports per-frame metrics. With `--save`,
MJPEG frames are written straight to `.jpg` files.

```bash
dotnet run --project examples/Periphery.Camera.Example -- capture --frames 60
dotnet run --project examples/Periphery.Camera.Example -- capture --frames 30 --save ./out
dotnet run --project examples/Periphery.Camera.Example -- capture --format nv12 --frames 10
```

### `controls [--device NAME] [--set KIND=VALUE] [--reset KIND]`

Read the current control table, optionally setting or resetting one. `KIND`
is any value of `CameraControlKind` (case-insensitive): `Brightness`,
`Contrast`, `Saturation`, `Sharpness`, `Hue`, `Gamma`, `WhiteBalance`,
`BacklightCompensation`, `Gain`, `Pan`, `Tilt`, `Zoom`, `Exposure`, `Focus`.

```bash
dotnet run --project examples/Periphery.Camera.Example -- controls
dotnet run --project examples/Periphery.Camera.Example -- controls --set Brightness=12
dotnet run --project examples/Periphery.Camera.Example -- controls --reset Exposure
```

### `host [--device NAME] [--seconds N]`

Long-running `DeviceSessionHost<CameraSession>` showing reconnect-resilient
lifecycle. While running, unplug and replug the camera — the printed status
will transition `SessionActive → DeviceAbsent → SessionStarting →
SessionActive`.

```bash
dotnet run --project examples/Periphery.Camera.Example -- host --seconds 60
```

## Notes

- All commands take an optional `--device NAME` that matches the first camera
  whose `Name` contains `NAME` (case-insensitive). Without it, the first
  enumerated camera is used.
- Capture timing is dominated by the camera and driver — high-resolution MJPEG
  modes can take several seconds to start streaming. The Periphery.Camera
  pipeline is decoupled (producer thread + bounded channel), so a slow
  consumer drops frames rather than blocking the driver. Watch the
  `dropped=` counter in capture output if the consumer is the bottleneck.
- Supported only on Windows today (Media Foundation backend). Linux V4L2 and
  macOS AVFoundation backends are described in
  [ADR-0035](../../docs/adr/0035-periphery-camera.md) but not yet implemented.
- The Windows backend uses **source-generated COM interop** end-to-end via
  `[GeneratedComInterface]`. If you hit `InvalidCastException` after
  modifying `MfInterop.cs` or `MfCameraBackend.cs`, the most likely cause
  is a wrong IID in a `[Guid("…")]` attribute — verify against the
  canonical Windows SDK header in
  [microsoft/win32metadata](https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders/um),
  not against natural-language docs. See
  [ADR-0037](../../docs/adr/0037-mf-sample-raw-vtable.md) and
  [`docs/patterns/source-generated-com-interop.md`](../../docs/patterns/source-generated-com-interop.md)
  Hazard A.
