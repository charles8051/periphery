# Periphery.Camera.OpenCvSharp

Hand OpenCV a frame from a camera you chose by identity, instead of by
`VideoCapture(0)`.

```sh
dotnet add package Periphery.Camera.OpenCvSharp --prerelease
```

OpenCV has no camera identity model. `VideoCapture(0)` is an index into whatever
order the operating system enumerated in, and it moves when a device is
replugged, when a virtual camera installs itself, or when a laptop lid opens. On
a machine with two identical cameras there is no argument you can pass that
means *the one on the left*. Periphery already answers that question, for every
device category, on all three platforms. The half that was missing was handing
the pixels to OpenCV, and `Periphery.Camera.OpenCvSharp`
is that half.

> **Capture is Windows and Linux.** Periphery enumerates cameras on all three
> platforms, but `CameraDevice` and `CameraSession` ship Windows and Linux
> backends only and throw `PlatformNotSupportedException` on macOS. The
> AVFoundation backend is planned, not written.

```csharp
using OpenCvSharp;
using Periphery;
using Periphery.Camera;
using Periphery.Camera.OpenCvSharp;

// Pick the camera by what it is, not by where it landed in a list.
var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithUsbId("046D", "0825")
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("That camera is not attached.");

var snapshot = await CameraDevice.ReadSnapshotAsync(device);
var format = snapshot.Formats
    .WithinBox(1280, 720)
    .PreferPixelFormat(CameraPixelFormat.Yuy2)
    .ThenByHighestFrameRate()
    .First();

await using var session = await CameraSession.OpenAsync(device, new CameraConfiguration(format));

await foreach (var frame in session.CaptureAsync())
{
    using (frame)
    using (var bgr = frame.ToBgr())     // any capture format -> CV_8UC3 BGR
    {
        Cv2.ImShow("preview", bgr);
        Cv2.WaitKey(1);
    }
}
```

No `VideoCapture`, no index, no format string. The camera was chosen by
vendor and product ID; it could equally have been chosen by serial number, by
name, by USB port path, or by a `DeviceWatcher` that hands you the camera the
moment someone plugs it in.

### Two identical cameras

This is the case the index model cannot express at all. Two of the same webcam
report the same name and the same VID/PID, so the only thing that separates them
is identity the OS assigns — a serial number if the device has one, and the
physical port path if it does not.

```csharp
var cameras = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithName("HD Pro Webcam C920")
    .ToListAsync();

// Real UVC cameras usually carry a serial; the ones that don't are told apart
// by the port they are plugged into, which is stable across reboots.
var left  = cameras.Single(c => c.SerialNumber == "A1B2C3D4");
var right = cameras.Single(c => c.SerialNumber == "E5F6A7B8");

// Both are the same model, so one format selection describes both.
var format = (await CameraDevice.ReadSnapshotAsync(left)).Formats
    .WithinBox(1280, 720)
    .PreferPixelFormat(CameraPixelFormat.Yuy2)
    .ThenByHighestFrameRate()
    .First();

await using var leftSession  = await CameraSession.OpenAsync(left,  new CameraConfiguration(format));
await using var rightSession = await CameraSession.OpenAsync(right, new CameraConfiguration(format));

// Two sessions, two independent capture loops, each pinned to a known lens.
```

Assigning a stereo pair the wrong way round is not a crash; it is a depth map
that is quietly inverted. An index cannot tell you which one you got, and a
serial number can.

### Three entry points, separated by who owns the pixels

| Call | Copies | Lifetime | Use it when |
|---|---|---|---|
| `frame.AsMat()` | no | valid inside the returned `MatScope` | you convert or measure inside the capture loop |
| `frame.ToMat()` | yes | you own the `Mat` | the raw capture format has to outlive the frame |
| `frame.ToBgr()` | yes | you own the `Mat` | you want an image rather than a capture format |

`AsMat` is the default. Wrapping costs nothing measurable, and a 1080p YUY2 to
BGR conversion is 0.126 ms against 1.83 ms for the clone `ToMat` has to make —
copying "to be safe" is fourteen times the cost of the conversion it is
supposedly protecting. The reason it returns a scope rather than a `Mat` is that
frames are pooled: once the lease is released the buffer is handed to the next
frame and refilled, and a `Mat` still pointing at it reads a later frame's
pixels rather than faulting. A type you have to dispose puts that decision in
the call-site syntax.

### The native payload is yours to pick

`Periphery.Camera.OpenCvSharp` references `OpenCvSharp4` — the managed binding —
and no `OpenCvSharp4.runtime.*` package. Install the payload for the platform
you deploy to: `OpenCvSharp4.runtime.win` on Windows,
`OpenCvSharp4.official.runtime.linux-x64` on Linux. Those are the two platforms
this package captures on. Without a payload the package restores and compiles,
and the first OpenCV call throws `DllNotFoundException`.

The package is named after the binding rather than the library, because
`OpenCvSharp4` and `Emgu.CV` are incompatible bindings of the same OpenCV and a
`Periphery.Camera.OpenCv` would claim a name a future Emgu package could not
share.

### MJPEG

MJPEG is the default 1080p30 mode on most UVC webcams and it has no `Mat` shape,
so the three methods split:

- **`AsMat` throws.** There is no header to build over a compressed blob, and
  therefore no zero-copy path to offer.
- **`ToMat` throws.** A byte-for-byte copy of JPEG is a `1 x n` vector of
  encoded bytes, which is not what "a copy of the frame's pixels" should hand
  back.
- **`ToBgr` decodes it**, straight from the frame's span with no intermediate
  `byte[]`. This is what makes `ToBgr` total over every format a camera can
  deliver, and it is why the package has a third method rather than two.

Both refusals name `ToBgr` in the message. The one other refusal is `Gray16` to
BGR: narrowing 16 bits to 8 needs a range, and a fixed `/257` renders most depth
and IR sensors black — take the CV_16UC1 `Mat` and apply `Cv2.Normalize` or
`Cv2.ConvertScaleAbs` yourself.

### Do not hold the lease through inference

Convert inside the lease; do the slow work outside it.

```csharp
await foreach (var frame in session.CaptureAsync())
{
    Mat bgr;
    using (frame)
        bgr = frame.ToBgr();        // ~0.1 ms, and the lease ends here

    await queue.Writer.WriteAsync(bgr);   // inference runs on the other side
}
```

A consumer slower than the frame interval parks the producer, because the
session's delivery channel is `BoundedChannelFullMode.Wait`. Frames are then
lost upstream inside Media Foundation or V4L2, where Periphery cannot count
them: `FramesDropped` stays at zero while the delivered rate halves. That stall
has no instrument today
([#322](https://github.com/charles8051/periphery/issues/322)), so the symptom is
a frame rate that is quietly wrong rather than a warning in a log.
