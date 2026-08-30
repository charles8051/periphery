// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera.Testing;

/// <summary>
/// Ready-made formats, controls, and a <see cref="DeviceInfo"/> for wiring up an
/// <see cref="InMemoryCameraBackend"/>. Convenience only — construct your own
/// <see cref="CameraFormat"/> / <see cref="CameraControlInfo"/> lists when a test
/// needs a specific capability set.
/// </summary>
public static class CameraTestFormats
{
    /// <summary>A single 640×480 YUY2 @30fps format — the common baseline.</summary>
    public static CameraFormat Vga { get; } =
        new(640, 480, CameraPixelFormat.Yuy2, new Rational(15), new Rational(30), CameraTransport.Uncompressed);

    /// <summary>A single 1920×1080 YUY2 @30fps format.</summary>
    public static CameraFormat Hd1080 { get; } =
        new(1920, 1080, CameraPixelFormat.Yuy2, new Rational(15), new Rational(30), CameraTransport.Uncompressed);

    /// <summary>
    /// A representative spread of advertised formats: YUY2 and MJPEG at VGA / 720p
    /// / 1080p plus an NV12 VGA — enough for format-selection logic to have real
    /// choices to make.
    /// </summary>
    public static IReadOnlyList<CameraFormat> Default { get; } =
    [
        new(640, 480, CameraPixelFormat.Yuy2, new Rational(15), new Rational(30), CameraTransport.Uncompressed),
        new(1280, 720, CameraPixelFormat.Yuy2, new Rational(15), new Rational(30), CameraTransport.Uncompressed),
        new(1920, 1080, CameraPixelFormat.Yuy2, new Rational(15), new Rational(30), CameraTransport.Uncompressed),
        new(640, 480, CameraPixelFormat.Mjpeg, new Rational(15), new Rational(60), CameraTransport.Compressed),
        new(1280, 720, CameraPixelFormat.Mjpeg, new Rational(15), new Rational(60), CameraTransport.Compressed),
        new(1920, 1080, CameraPixelFormat.Mjpeg, new Rational(15), new Rational(30), CameraTransport.Compressed),
        new(640, 480, CameraPixelFormat.Nv12, new Rational(15), new Rational(30), CameraTransport.Uncompressed),
    ];

    /// <summary>A representative control set spanning read/write, read-only, and
    /// auto-capable controls.</summary>
    public static IReadOnlyList<CameraControlInfo> DefaultControls { get; } =
    [
        new(CameraControlKind.Brightness, "Brightness", -64, 64, 1, 0, false, false),
        new(CameraControlKind.Contrast, "Contrast", 0, 100, 1, 50, false, false),
        new(CameraControlKind.Exposure, "Exposure", -13, -1, 1, -5, true, false),
        new(CameraControlKind.Focus, "Focus", 0, 255, 5, 0, true, false),
        new(CameraControlKind.Gain, "Gain", 0, 255, 1, 0, false, true),
    ];

    /// <summary>A camera <see cref="DeviceInfo"/> suitable for the enumeration /
    /// open path under an installed <see cref="CameraTestScope"/>.</summary>
    public static DeviceInfo CreateDeviceInfo(string id = "TEST\\CAM\\0001", string name = "Test Camera") =>
        new() { Id = id, Name = name, Category = DeviceCategory.Camera };
}
