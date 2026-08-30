// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Camera.Linux;

/// <summary>
/// Pure mapping between V4L2 fourcc pixel formats / control IDs and the
/// platform-neutral <see cref="CameraPixelFormat"/> /
/// <see cref="CameraControlKind"/> enums — the Linux counterpart of
/// <c>Windows.MfFormatMap</c>.
/// </summary>
internal static class V4l2FormatMap
{
    /// <summary>v4l2_fourcc(a, b, c, d) — little-endian packed four-character code.</summary>
    internal static uint FourCc(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    internal static string FourCcToString(uint fourcc) => new(
    [
        (char)(fourcc & 0xFF),
        (char)((fourcc >> 8) & 0xFF),
        (char)((fourcc >> 16) & 0xFF),
        (char)((fourcc >> 24) & 0xFF),
    ]);

    // Compressed
    private static readonly uint Mjpg = FourCc('M', 'J', 'P', 'G');
    private static readonly uint Jpeg = FourCc('J', 'P', 'E', 'G');
    // Packed YUV
    private static readonly uint Yuyv = FourCc('Y', 'U', 'Y', 'V');
    private static readonly uint Uyvy = FourCc('U', 'Y', 'V', 'Y');
    // Planar YUV
    private static readonly uint Nv12 = FourCc('N', 'V', '1', '2');
    private static readonly uint Nv21 = FourCc('N', 'V', '2', '1');
    private static readonly uint Yu12 = FourCc('Y', 'U', '1', '2'); // I420
    private static readonly uint Yv12 = FourCc('Y', 'V', '1', '2');
    // RGB / BGR packed
    private static readonly uint Bgr3 = FourCc('B', 'G', 'R', '3');
    private static readonly uint Rgb3 = FourCc('R', 'G', 'B', '3');
    // Grayscale
    private static readonly uint Grey = FourCc('G', 'R', 'E', 'Y');
    private static readonly uint Y16 = FourCc('Y', '1', '6', ' ');

    /// <summary>
    /// Maps a V4L2 fourcc to the neutral enum. Returns false for formats the
    /// library has no neutral representation for (enumeration skips them) —
    /// notably the legacy 32-bit RGB fourccs whose alpha position V4L2
    /// historically left ill-defined.
    /// </summary>
    internal static bool TryMapPixelFormat(uint fourcc, out CameraPixelFormat format)
    {
        if (fourcc == Mjpg || fourcc == Jpeg) { format = CameraPixelFormat.Mjpeg; return true; }
        if (fourcc == Yuyv) { format = CameraPixelFormat.Yuy2; return true; }
        if (fourcc == Uyvy) { format = CameraPixelFormat.Uyvy; return true; }
        if (fourcc == Nv12) { format = CameraPixelFormat.Nv12; return true; }
        if (fourcc == Nv21) { format = CameraPixelFormat.Nv21; return true; }
        if (fourcc == Yu12) { format = CameraPixelFormat.I420; return true; }
        if (fourcc == Yv12) { format = CameraPixelFormat.Yv12; return true; }
        if (fourcc == Bgr3) { format = CameraPixelFormat.Bgr24; return true; }
        if (fourcc == Rgb3) { format = CameraPixelFormat.Rgb24; return true; }
        if (fourcc == Grey) { format = CameraPixelFormat.Gray8; return true; }
        if (fourcc == Y16) { format = CameraPixelFormat.Gray16; return true; }
        format = CameraPixelFormat.Unknown;
        return false;
    }

    /// <summary>Maps the neutral enum back to the V4L2 fourcc for S_FMT.</summary>
    internal static bool TryMapToFourCc(CameraPixelFormat format, out uint fourcc)
    {
        fourcc = format switch
        {
            CameraPixelFormat.Mjpeg => Mjpg,
            CameraPixelFormat.Yuy2 => Yuyv,
            CameraPixelFormat.Uyvy => Uyvy,
            CameraPixelFormat.Nv12 => Nv12,
            CameraPixelFormat.Nv21 => Nv21,
            CameraPixelFormat.I420 => Yu12,
            CameraPixelFormat.Yv12 => Yv12,
            CameraPixelFormat.Bgr24 => Bgr3,
            CameraPixelFormat.Rgb24 => Rgb3,
            CameraPixelFormat.Gray8 => Grey,
            CameraPixelFormat.Gray16 => Y16,
            _ => 0,
        };
        return fourcc != 0;
    }

    /// <summary>
    /// Interpret an auto-companion control's value as a mode for
    /// <paramref name="kind"/>.
    /// </summary>
    /// <remarks>
    /// Per-control because V4L2 is not consistent here. <c>V4L2_CID_AUTOGAIN</c>,
    /// <c>V4L2_CID_AUTO_WHITE_BALANCE</c> and <c>V4L2_CID_FOCUS_AUTO</c> are
    /// booleans where 1 means automatic. <c>V4L2_CID_EXPOSURE_AUTO</c> is an
    /// enumeration running the other way — 0 is automatic, 1 is manual — with two
    /// further priority modes in which only one half of the exposure is driven by
    /// the device. Shutter priority holds the exposure <i>time</i>, which is what
    /// <see cref="CameraControlKind.Exposure"/> names, so it reads as manual;
    /// aperture priority leaves the time to the device, so it reads as automatic.
    /// </remarks>
    /// <summary>
    /// The value to write to <paramref name="kind"/>'s auto-companion control to
    /// put it into <paramref name="mode"/>. The inverse of
    /// <see cref="InterpretAutoValue"/>.
    /// </summary>
    /// <remarks>
    /// Needed because writing a value on V4L2 does not, by itself, take a control
    /// away from the device: the auto loop owns it until its companion says
    /// otherwise, so a write either fails with <c>EBUSY</c> or is overwritten on
    /// the next frame. Media Foundation gets this for free by passing
    /// <c>MF_CAMERA_FLAGS_MANUAL</c> on the same call.
    /// </remarks>
    /// <summary>
    /// The auto-companion values that would put <paramref name="kind"/> into
    /// <paramref name="mode"/>, in preference order — most preferred first.
    /// </summary>
    /// <remarks>
    /// Exists because <c>V4L2_CID_EXPOSURE_AUTO</c> is a <b>menu</b> and a device need not
    /// advertise every entry. <c>uvcvideo</c> builds the menu mask from the device's UVC
    /// <c>bmAutoExposureMode</c> bitmap, and many webcams offer only <c>MANUAL</c> (1) and
    /// <c>APERTURE_PRIORITY</c> (3) — writing 0 or 2 to those returns <c>EINVAL</c> (issue #275).
    /// <para>
    /// <see cref="MapModeToAutoValue"/> could only ever emit 0 for automatic, so on such a
    /// camera <c>ResetControlAsync</c> failed <em>destructively</em>: it took exposure off
    /// automatic on the way through and then threw rather than handing it back. Meanwhile
    /// <see cref="InterpretAutoValue"/> already accepted all four values, so the read side
    /// acknowledged a state the write side could not produce.
    /// </para>
    /// <para>
    /// Ordering is by fidelity, not by numeric value. <c>AUTO</c> and <c>MANUAL</c> are what the
    /// caller asked for; the priority modes are the device driving one half of the exposure
    /// equation, which <see cref="InterpretAutoValue"/> already folds into the same two modes,
    /// so they are honest fallbacks rather than silent substitutions.
    /// </para>
    /// <para>
    /// Pure and total: which of these the device actually accepts is a question for the backend,
    /// which owns the fd (<c>VIDIOC_QUERYMENU</c>).
    /// </para>
    /// </remarks>
    internal static int[] AutoValueCandidates(CameraControlKind kind, CameraControlMode mode)
    {
        if (kind != CameraControlKind.Exposure)
            return [MapModeToAutoValue(kind, mode)];

        return mode == CameraControlMode.Automatic
            ? [V4l2Interop.V4L2_EXPOSURE_AUTO_MODE, V4l2Interop.V4L2_EXPOSURE_APERTURE_PRIORITY]
            : [V4l2Interop.V4L2_EXPOSURE_MANUAL, V4l2Interop.V4L2_EXPOSURE_SHUTTER_PRIORITY];
    }

    internal static int MapModeToAutoValue(CameraControlKind kind, CameraControlMode mode) =>
        kind == CameraControlKind.Exposure
            ? mode == CameraControlMode.Automatic
                ? V4l2Interop.V4L2_EXPOSURE_AUTO_MODE
                : V4l2Interop.V4L2_EXPOSURE_MANUAL
            : mode == CameraControlMode.Automatic ? 1 : 0;

    internal static CameraControlMode InterpretAutoValue(CameraControlKind kind, int autoValue) =>
        kind == CameraControlKind.Exposure
            ? autoValue switch
            {
                V4l2Interop.V4L2_EXPOSURE_MANUAL => CameraControlMode.Manual,
                V4l2Interop.V4L2_EXPOSURE_SHUTTER_PRIORITY => CameraControlMode.Manual,
                V4l2Interop.V4L2_EXPOSURE_AUTO_MODE => CameraControlMode.Automatic,
                V4l2Interop.V4L2_EXPOSURE_APERTURE_PRIORITY => CameraControlMode.Automatic,
                _ => CameraControlMode.Unknown,
            }
            : autoValue != 0 ? CameraControlMode.Automatic : CameraControlMode.Manual;

    /// <summary>
    /// Maps a control kind to its V4L2 control ID plus, where one exists, the
    /// companion auto-mode control whose presence sets
    /// <see cref="CameraControlInfo.SupportsAutoMode"/>.
    /// </summary>
    internal static bool TryGetControlId(CameraControlKind kind, out uint id, out uint autoId)
    {
        autoId = 0;
        switch (kind)
        {
            case CameraControlKind.Brightness: id = V4l2Interop.V4L2_CID_BRIGHTNESS; return true;
            case CameraControlKind.Contrast: id = V4l2Interop.V4L2_CID_CONTRAST; return true;
            case CameraControlKind.Saturation: id = V4l2Interop.V4L2_CID_SATURATION; return true;
            case CameraControlKind.Hue: id = V4l2Interop.V4L2_CID_HUE; return true;
            case CameraControlKind.Gamma: id = V4l2Interop.V4L2_CID_GAMMA; return true;
            case CameraControlKind.Sharpness: id = V4l2Interop.V4L2_CID_SHARPNESS; return true;
            case CameraControlKind.BacklightCompensation: id = V4l2Interop.V4L2_CID_BACKLIGHT_COMPENSATION; return true;
            case CameraControlKind.PowerLineFrequency: id = V4l2Interop.V4L2_CID_POWER_LINE_FREQUENCY; return true;
            case CameraControlKind.Gain:
                id = V4l2Interop.V4L2_CID_GAIN;
                autoId = V4l2Interop.V4L2_CID_AUTOGAIN;
                return true;
            case CameraControlKind.WhiteBalance:
                id = V4l2Interop.V4L2_CID_WHITE_BALANCE_TEMPERATURE;
                autoId = V4l2Interop.V4L2_CID_AUTO_WHITE_BALANCE;
                return true;
            case CameraControlKind.Exposure:
                id = V4l2Interop.V4L2_CID_EXPOSURE_ABSOLUTE;
                autoId = V4l2Interop.V4L2_CID_EXPOSURE_AUTO;
                return true;
            case CameraControlKind.Focus:
                id = V4l2Interop.V4L2_CID_FOCUS_ABSOLUTE;
                autoId = V4l2Interop.V4L2_CID_FOCUS_AUTO;
                return true;
            case CameraControlKind.Zoom: id = V4l2Interop.V4L2_CID_ZOOM_ABSOLUTE; return true;
            case CameraControlKind.Pan: id = V4l2Interop.V4L2_CID_PAN_ABSOLUTE; return true;
            case CameraControlKind.Tilt: id = V4l2Interop.V4L2_CID_TILT_ABSOLUTE; return true;
            default: id = 0; return false;
        }
    }

    /// <summary>The probe order for control enumeration — every mappable kind.</summary>
    internal static ReadOnlySpan<CameraControlKind> EnumerableControlKinds =>
    [
        CameraControlKind.Brightness,
        CameraControlKind.Contrast,
        CameraControlKind.Saturation,
        CameraControlKind.Sharpness,
        CameraControlKind.Gain,
        CameraControlKind.Exposure,
        CameraControlKind.WhiteBalance,
        CameraControlKind.Focus,
        CameraControlKind.Zoom,
        CameraControlKind.Pan,
        CameraControlKind.Tilt,
        CameraControlKind.Gamma,
        CameraControlKind.Hue,
        CameraControlKind.BacklightCompensation,
        CameraControlKind.PowerLineFrequency,
    ];
}
