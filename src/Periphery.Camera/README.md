# Periphery.Camera

Capture frames from a camera you chose by identity. Supports Windows (Media Foundation) and Linux (V4L2), not macOS.

```sh
dotnet add package Periphery.Camera --prerelease
```

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

Dispose every frame. Frames come from a fixed pool, and an undisposed frame is a buffer the pool never gets back.

## Options

```csharp
CameraSession.For(device).WithSessionOptions(o => o with { BufferCount = 6 })
```

| Option | Meaning | Default |
|---|---|---|
| `BufferCount` | Frames you may hold at once | 3 |
| `QueueDepth` | Frames queued between capture and your loop | 1 |
| `ExhaustionPolicy` | `LatestWins` drops the oldest queued frame when you fall behind; `StallProducer` waits for you | `LatestWins` |

`session.Metrics.FramesDropped` counts the frames a session drops.

To keep frames beyond the loop, or to feed several consumers from one camera, see
[Camera fan-out and retention](https://github.com/charles8051/periphery/blob/main/docs/surface/camera-fan-out-and-retention.md).

## Related packages

| Package | What it adds |
|---|---|
| [`Periphery.Camera.Avalonia`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Avalonia) | A `CameraPreview` control for Avalonia |
| [`Periphery.Camera.OpenCvSharp`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.OpenCvSharp) | Frames as OpenCV `Mat`s, without `VideoCapture` |
| [`Periphery.Camera.Testing`](https://github.com/charles8051/periphery/tree/main/src/Periphery.Camera.Testing) | A hardware-free camera backend for tests |
