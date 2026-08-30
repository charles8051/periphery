// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// P/Invoke declarations for the Windows DisplayConfig APIs (user32.dll) used
/// to enumerate active display paths and retrieve per-monitor properties
/// (friendly name, connector type, preferred resolution) without requiring a
/// Windows-specific TFM.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DisplayConfigInterop
{
    // ── QueryDisplayConfig flags ────────────────────────────────────────
    internal const uint QDC_ONLY_ACTIVE_PATHS = 2;

    // ── DISPLAYCONFIG_DEVICE_INFO_TYPE constants ────────────────────────
    internal const int DEVICE_INFO_GET_TARGET_NAME           = 2;
    internal const int DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    // ── DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values ────────────────────
    //
    // The complete wingdi.h enumeration. It is enumerated in full deliberately:
    // the mappers below classify unrecognised values as "unknown" rather than
    // guessing, which is only honest if every value Windows actually reports is
    // named here. (7 is unused by the SDK; the gap is not an omission.)
    internal const int OUTPUT_TECH_OTHER            = -1;   // also _FORCE_UINT32 (0xFFFFFFFF)
    internal const int OUTPUT_TECH_HD15             =  0;   // VGA / HD-15
    internal const int OUTPUT_TECH_SVIDEO           =  1;
    internal const int OUTPUT_TECH_COMPOSITE_VIDEO  =  2;
    internal const int OUTPUT_TECH_COMPONENT_VIDEO  =  3;
    internal const int OUTPUT_TECH_DVI              =  4;
    internal const int OUTPUT_TECH_HDMI             =  5;
    internal const int OUTPUT_TECH_LVDS             =  6;   // internal LCD panel
    internal const int OUTPUT_TECH_D_JPN            =  8;   // Japanese D-terminal (analogue)
    internal const int OUTPUT_TECH_SDI              =  9;
    internal const int OUTPUT_TECH_DP_EXTERNAL      = 10;   // DisplayPort (external)
    internal const int OUTPUT_TECH_DP_EMBEDDED      = 11;   // eDP (internal / laptop)
    internal const int OUTPUT_TECH_UDI_EXTERNAL     = 12;   // Unified Display Interface (external)
    internal const int OUTPUT_TECH_UDI_EMBEDDED     = 13;   // UDI (internal — see SDK Remarks)
    internal const int OUTPUT_TECH_SDTVDONGLE       = 14;   // SDTV dongle cable
    internal const int OUTPUT_TECH_MIRACAST         = 15;   // wireless
    internal const int OUTPUT_TECH_INDIRECT_WIRED   = 16;   // IddCx indirect display (wired transport)
    internal const int OUTPUT_TECH_INDIRECT_VIRTUAL = 17;   // virtual / remote
    internal const int OUTPUT_TECH_DP_USB_TUNNEL    = 18;   // DisplayPort tunnelled over USB4
    internal const int OUTPUT_TECH_INTERNAL         = unchecked((int)0x80000000);

    // ── Structs ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int  HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    // sizeof = 20 bytes
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;   //  8
        public uint Id;          //  4
        public uint ModeInfoIdx; //  4  (union — only simple form needed)
        public uint StatusFlags; //  4
    }

    // sizeof = 48 bytes
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathTargetInfo
    {
        public Luid                AdapterId;       //  8
        public uint                Id;              //  4
        public uint                ModeInfoIdx;     //  4  (union)
        public int                 OutputTechnology;//  4  (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY)
        public int                 Rotation;        //  4  (DISPLAYCONFIG_ROTATION_*, 1..4)
        public int                 Scaling;         //  4
        public DisplayConfigRational RefreshRate;   //  8
        public int                 ScanLineOrdering;//  4
        public int                 TargetAvailable; //  4  (BOOL)
        public uint                StatusFlags;     //  4
    }

    // sizeof = 72 bytes
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo; // 20
        public DisplayConfigPathTargetInfo TargetInfo; // 48
        public uint                        Flags;      //  4
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO (64 bytes), Explicit layout.
    /// The union at offset 16 is either a source mode (type=1) or target mode (type=2).
    /// Only the source-mode fields are exposed here — they supply the current active
    /// resolution and virtual-desktop position needed for <see cref="DeviceInfo.DisplayBounds"/>.
    /// <para>
    /// <b>The two are in different frames of reference.</b> <c>SourcePositionX/Y</c> is
    /// the origin Windows laid the monitor out at, computed from its <i>rotated</i>
    /// footprint; <c>SourceWidth/Height</c> is the source surface, which rotation does
    /// <i>not</i> transpose. Reconcile them through
    /// <see cref="DisplayGeometry.DesktopBounds"/> with the path's
    /// <c>TargetInfo.Rotation</c> — never combine them verbatim (issue #163).
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DisplayConfigModeInfo
    {
        internal const int TYPE_SOURCE = 1;

        [FieldOffset(0)]  public int  InfoType;          // DISPLAYCONFIG_MODE_INFO_TYPE
        [FieldOffset(4)]  public uint Id;
        [FieldOffset(8)]  public Luid AdapterId;         // 8 bytes; union follows at offset 16
        // DISPLAYCONFIG_SOURCE_MODE fields (union offset 0–19):
        [FieldOffset(16)] public uint SourceWidth;
        [FieldOffset(20)] public uint SourceHeight;
        [FieldOffset(24)] public int  SourcePixelFormat; // DISPLAYCONFIG_PIXELFORMAT (unused)
        [FieldOffset(28)] public int  SourcePositionX;   // POINTL.x — virtual desktop left
        [FieldOffset(32)] public int  SourcePositionY;   // POINTL.y — virtual desktop top
        // Remaining bytes covered by Size = 64
    }

    // sizeof = 20 bytes
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigDeviceInfoHeader
    {
        public int  Type;      //  4  (DISPLAYCONFIG_DEVICE_INFO_TYPE)
        public uint Size;      //  4
        public Luid AdapterId; //  8
        public uint Id;        //  4
    }

    /// <summary>
    /// DISPLAYCONFIG_TARGET_DEVICE_NAME (420 bytes).
    /// Unsafe struct required for the two fixed WCHAR arrays.
    /// </summary>
    internal unsafe struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;             //  20
        public uint   Flags;                                     //   4
        public int    OutputTechnology;                          //   4
        public ushort EdidManufactureId;                         //   2
        public ushort EdidProductCodeId;                         //   2
        public uint   ConnectorInstance;                         //   4
        public fixed char MonitorFriendlyDeviceName[64];         // 128  (64 × WCHAR)
        public fixed char MonitorDevicePath[128];                // 256  (128 × WCHAR)
        // Total: 420 bytes
    }

    /// <summary>
    /// DISPLAYCONFIG_TARGET_PREFERRED_MODE (80 bytes).
    /// Layout: header(20) + width(4) + height(4) + 4-byte padding + targetMode(48) = 80.
    /// The 4-byte padding is required because DISPLAYCONFIG_TARGET_MODE begins with
    /// DISPLAYCONFIG_VIDEO_SIGNAL_INFO.pixelRate (UINT64, 8-byte alignment), so the
    /// compiler pads the struct to align targetMode at offset 32.
    /// Passing size=76 causes DisplayConfigGetDeviceInfo to return ERROR_INVALID_PARAMETER.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    internal struct DisplayConfigTargetPreferredMode
    {
        public DisplayConfigDeviceInfoHeader Header; // offset  0, 20 bytes
        public uint Width;                           // offset 20,  4 bytes
        public uint Height;                          // offset 24,  4 bytes
        // 4 bytes padding (offset 28–31) for 8-byte alignment of targetMode
        // DISPLAYCONFIG_TARGET_MODE (48 bytes) at offset 32 — not accessed
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────

    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(
        uint  flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    internal static partial int QueryDisplayConfig(
        uint                     flags,
        ref uint                 numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint                 numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr                   currentTopologyId);

    /// <summary>
    /// Calls DisplayConfigGetDeviceInfo with a raw pointer to any struct whose
    /// first field is a <see cref="DisplayConfigDeviceInfoHeader"/>.
    /// The caller is responsible for setting <c>Header.Type</c> and <c>Header.Size</c>.
    /// </summary>
    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(void* requestPacket);
}
