// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;

namespace Periphery.Camera.Windows;

/// <summary>
/// Bidirectional mapping between Media Foundation video format GUIDs and
/// <see cref="CameraPixelFormat"/> values, plus MF control property enums
/// to <see cref="CameraControlKind"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MfFormatMap
{
    // ═══════════════════════════════════════════════════════════════════
    // Pixel format mapping
    // ═══════════════════════════════════════════════════════════════════

    private static readonly Dictionary<Guid, (CameraPixelFormat Format, CameraTransport Transport)> s_guidToFormat = new()
    {
        [MfInterop.MFVideoFormat_MJPG]   = (CameraPixelFormat.Mjpeg,  CameraTransport.Compressed),
        [MfInterop.MFVideoFormat_YUY2]   = (CameraPixelFormat.Yuy2,   CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_UYVY]   = (CameraPixelFormat.Uyvy,   CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_NV12]   = (CameraPixelFormat.Nv12,   CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_NV21]   = (CameraPixelFormat.Nv21,   CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_I420]   = (CameraPixelFormat.I420,   CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_YV12]   = (CameraPixelFormat.Yv12,   CameraTransport.Uncompressed),
        // Windows MFVideoFormat_RGB24 stores pixels as BGR in memory.
        [MfInterop.MFVideoFormat_RGB24]  = (CameraPixelFormat.Bgr24,  CameraTransport.Uncompressed),
        // MFVideoFormat_RGB32 is BGRX (X = padding), closest to Bgra32.
        [MfInterop.MFVideoFormat_RGB32]  = (CameraPixelFormat.Bgra32, CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_ARGB32] = (CameraPixelFormat.Argb32, CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_L8]     = (CameraPixelFormat.Gray8,  CameraTransport.Uncompressed),
        [MfInterop.MFVideoFormat_L16]    = (CameraPixelFormat.Gray16, CameraTransport.Uncompressed),
    };

    private static readonly Dictionary<CameraPixelFormat, Guid> s_formatToGuid;

    static MfFormatMap()
    {
        s_formatToGuid = new Dictionary<CameraPixelFormat, Guid>();
        foreach (var (guid, (format, _)) in s_guidToFormat)
        {
            s_formatToGuid.TryAdd(format, guid);
        }
    }

    internal static bool TryMapSubtype(Guid subtype, out CameraPixelFormat format, out CameraTransport transport)
    {
        if (s_guidToFormat.TryGetValue(subtype, out var entry))
        {
            format = entry.Format;
            transport = entry.Transport;
            return true;
        }
        format = CameraPixelFormat.Unknown;
        transport = CameraTransport.Uncompressed;
        return false;
    }

    internal static bool TryMapFormat(CameraPixelFormat format, out Guid subtype)
    {
        return s_formatToGuid.TryGetValue(format, out subtype);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Camera control mapping (IAMCameraControl property IDs)
    // ═══════════════════════════════════════════════════════════════════

    internal const int CameraControl_Pan = 0;
    internal const int CameraControl_Tilt = 1;
    internal const int CameraControl_Zoom = 3;
    internal const int CameraControl_Exposure = 4;
    internal const int CameraControl_Focus = 6;

    private static readonly (int PropertyId, CameraControlKind Kind, bool IsCameraControl)[] s_cameraControls =
    [
        (CameraControl_Pan,      CameraControlKind.Pan,      true),
        (CameraControl_Tilt,     CameraControlKind.Tilt,     true),
        (CameraControl_Zoom,     CameraControlKind.Zoom,     true),
        (CameraControl_Exposure, CameraControlKind.Exposure,  true),
        (CameraControl_Focus,    CameraControlKind.Focus,     true),
    ];

    // ═══════════════════════════════════════════════════════════════════
    // Video proc amp mapping (IAMVideoProcAmp property IDs)
    // ═══════════════════════════════════════════════════════════════════

    internal const int VideoProcAmp_Brightness = 0;
    internal const int VideoProcAmp_Contrast = 1;
    internal const int VideoProcAmp_Hue = 2;
    internal const int VideoProcAmp_Saturation = 3;
    internal const int VideoProcAmp_Sharpness = 4;
    internal const int VideoProcAmp_Gamma = 5;
    internal const int VideoProcAmp_WhiteBalance = 7;
    internal const int VideoProcAmp_BacklightCompensation = 8;
    internal const int VideoProcAmp_Gain = 9;

    private static readonly (int PropertyId, CameraControlKind Kind, bool IsCameraControl)[] s_procAmpControls =
    [
        (VideoProcAmp_Brightness,            CameraControlKind.Brightness,            false),
        (VideoProcAmp_Contrast,              CameraControlKind.Contrast,              false),
        (VideoProcAmp_Hue,                   CameraControlKind.Hue,                   false),
        (VideoProcAmp_Saturation,            CameraControlKind.Saturation,            false),
        (VideoProcAmp_Sharpness,             CameraControlKind.Sharpness,             false),
        (VideoProcAmp_Gamma,                 CameraControlKind.Gamma,                 false),
        (VideoProcAmp_WhiteBalance,          CameraControlKind.WhiteBalance,          false),
        (VideoProcAmp_BacklightCompensation, CameraControlKind.BacklightCompensation, false),
        (VideoProcAmp_Gain,                  CameraControlKind.Gain,                  false),
    ];

    /// <summary>
    /// Returns all known control descriptors (both IAMCameraControl and IAMVideoProcAmp).
    /// The caller probes each against the device to see which are actually supported.
    /// </summary>
    internal static IReadOnlyList<(int PropertyId, CameraControlKind Kind, bool IsCameraControl)> AllKnownControls
    {
        get
        {
            var all = new List<(int, CameraControlKind, bool)>(s_cameraControls.Length + s_procAmpControls.Length);
            all.AddRange(s_cameraControls);
            all.AddRange(s_procAmpControls);
            return all;
        }
    }

    /// <summary>
    /// Finds the MF property ID and interface type for a <see cref="CameraControlKind"/>.
    /// </summary>
    internal static bool TryGetPropertyId(CameraControlKind kind, out int propertyId, out bool isCameraControl)
    {
        foreach (var entry in s_cameraControls)
        {
            if (entry.Kind == kind)
            {
                propertyId = entry.PropertyId;
                isCameraControl = true;
                return true;
            }
        }
        foreach (var entry in s_procAmpControls)
        {
            if (entry.Kind == kind)
            {
                propertyId = entry.PropertyId;
                isCameraControl = false;
                return true;
            }
        }
        propertyId = -1;
        isCameraControl = false;
        return false;
    }
}
