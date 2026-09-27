# Periphery.Camera.OpenCvSharp

Hand OpenCV frames from a camera you chose by identity, instead of by `VideoCapture(0)`.

```sh
dotnet add package Periphery.Camera.OpenCvSharp --prerelease
dotnet add package OpenCvSharp4.runtime.win    # Linux: OpenCvSharp4.official.runtime.linux-x64
```

This package does not bring OpenCV's native binaries. Without a runtime package, the first OpenCV call throws `DllNotFoundException`. Capture supports Windows and Linux.

`VideoCapture(0)` is a position in the OS enumeration order. It changes when a device is replugged or a virtual camera installs, and it cannot tell two identical cameras apart. Periphery selects the camera by VID/PID, serial number, name, or port.

```csharp
using OpenCvSharp;
using Periphery;
using Periphery.Camera;
using Periphery.Camera.OpenCvSharp;

var device = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Camera)
    .WithUsbId("046D", "0825")          // or .WithSerialNumber("A1B2C3D4") for one of two identical cameras
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("That camera is not attached.");

await using var session = await CameraSession.For(device)
    .MaxResolution(1280, 720)
    .OpenAsync();

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

## Converting frames

| Call | Copies | Use it when |
|---|---|---|
| `frame.AsMat()` | no | You convert or measure inside the capture loop. The `Mat` is valid until the returned scope is disposed. |
| `frame.ToMat()` | yes | You need the raw capture format after the frame is released. |
| `frame.ToBgr()` | yes | You want a BGR image. This is the only one that accepts MJPEG. |

Convert inside the `using (frame)` block, and run slow work such as inference after it. A frame you hold keeps a buffer from the camera's pool.
