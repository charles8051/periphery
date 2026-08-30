// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Monitor.Windows;

/// <summary>
/// P/Invoke declarations for the Windows monitor backends: DisplayConfig
/// (identity correlation), dxva2 (physical-monitor VCP), and the GDI
/// display-mode surface. All declarations use
/// <see cref="LibraryImportAttribute"/> for AOT/trim safety.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class MonitorInterop
{
    // ── DisplayConfig (user32) ─────────────────────────────────────────

    internal const uint QDC_ONLY_ACTIVE_PATHS = 2;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
    internal const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    // CCD rotation base (DISPLAYCONFIG_ROTATION_IDENTITY..ROTATE270 = 1..4).
    // Translation to/from the platform-neutral MonitorOrientation contract lives
    // in CcdOrientation, not in this constant's numbering (ADR-0064).
    internal const uint DISPLAYCONFIG_ROTATION_IDENTITY = 1;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values carried by
    // DisplayConfigTargetDeviceName.OutputTechnology. Verified verbatim against
    // the Windows SDK: Include/10.0.26100.0/um/wingdi.h lines 2807-2828 (the
    // same values core's own DisplayConfigInterop.OUTPUT_TECH_* already ships).
    // Note the enum SKIPS 7, so it is not densely packed -- do not "correct"
    // these by counting members. Translation to the
    // platform-neutral MonitorOutputTechnology contract lives in
    // CcdOutputTechnology, not in this numbering (ADR-0064 / ADR-0070). Only the
    // values the contract maps are named; everything else reads as Other. Note
    // an IddCx indirect (virtual) display reports INDIRECT_WIRED, not
    // INDIRECT_VIRTUAL — both map to Virtual.
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HD15 = 0;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI = 4;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI = 5;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL = 10;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED = 16;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL = 17;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    // SetDisplayConfig flags.
    internal const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x0020;
    internal const uint SDC_VALIDATE = 0x0040;
    internal const uint SDC_APPLY = 0x0080;
    internal const uint SDC_SAVE_TO_DATABASE = 0x0200;
    internal const uint SDC_ALLOW_CHANGES = 0x0400;

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO with both union views at their canonical
    /// offsets: the source view (desktop-space size + virtual position) and
    /// the target view (video signal timing).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;

        // Source view (InfoType == SOURCE).
        [FieldOffset(16)] public uint SourceWidth;
        [FieldOffset(20)] public uint SourceHeight;
        [FieldOffset(24)] public uint SourcePixelFormat;
        [FieldOffset(28)] public int SourcePositionX;
        [FieldOffset(32)] public int SourcePositionY;

        // Target view (InfoType == TARGET) — video signal info.
        [FieldOffset(16)] public ulong TargetPixelRate;
        [FieldOffset(24)] public DisplayConfigRational TargetHSyncFreq;
        [FieldOffset(32)] public DisplayConfigRational TargetVSyncFreq;
        [FieldOffset(40)] public uint TargetActiveSizeCx;
        [FieldOffset(44)] public uint TargetActiveSizeCy;
        [FieldOffset(48)] public uint TargetTotalSizeCx;
        [FieldOffset(52)] public uint TargetTotalSizeCy;
        [FieldOffset(56)] public uint TargetVideoStandard;
        [FieldOffset(60)] public uint TargetScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigTargetPreferredMode
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Width;
        public uint Height;
        // DISPLAYCONFIG_TARGET_MODE = video signal info (48 bytes).
        public ulong PixelRate;
        public DisplayConfigRational HSyncFreq;
        public DisplayConfigRational VSyncFreq;
        public uint ActiveSizeCx;
        public uint ActiveSizeCy;
        public uint TotalSizeCx;
        public uint TotalSizeCy;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [LibraryImport("user32.dll")]
    internal static unsafe partial int SetDisplayConfig(
        uint numPathArrayElements, DisplayConfigPathInfo* pathArray,
        uint numModeInfoArrayElements, DisplayConfigModeInfo* modeInfoArray,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    internal static unsafe partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements, DisplayConfigPathInfo* pathArray,
        ref uint numModeInfoArrayElements, DisplayConfigModeInfo* modeInfoArray,
        IntPtr currentTopologyId);

    [LibraryImport("user32.dll")]
    internal static unsafe partial int DisplayConfigGetDeviceInfo(void* requestPacket);

    // ── Monitor handles (user32) ───────────────────────────────────────

    // MonitorInfoEx.Flags: this is the primary monitor. Windows sets it on
    // exactly one monitor — the authoritative single-primary signal (issue #138).
    internal const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clipRect,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, int> enumProc,
        IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetMonitorInfo(IntPtr hMonitor, MonitorInfoEx* info);

    // ── Physical monitors + low-level VCP (dxva2) ──────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct PhysicalMonitor
    {
        public IntPtr Handle;
        public fixed char Description[128];
    }

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, out uint count);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint count, PhysicalMonitor* monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyPhysicalMonitor(IntPtr handle);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr handle, byte vcpCode, out uint codeType, out uint currentValue, out uint maximumValue);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetVCPFeature(IntPtr handle, byte vcpCode, uint newValue);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCapabilitiesStringLength(IntPtr handle, out uint length);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CapabilitiesRequestAndCapabilitiesReply(
        IntPtr handle, byte* asciiCapabilities, uint length);

    // ── Display modes (user32) ─────────────────────────────────────────

    internal const int ENUM_CURRENT_SETTINGS = -1;

    internal const uint DM_DISPLAYORIENTATION = 0x0000_0080;
    internal const uint DM_BITSPERPEL = 0x0004_0000;
    internal const uint DM_PELSWIDTH = 0x0008_0000;
    internal const uint DM_PELSHEIGHT = 0x0010_0000;
    internal const uint DM_DISPLAYFREQUENCY = 0x0040_0000;

    internal const uint CDS_UPDATEREGISTRY = 0x0000_0001;
    internal const uint CDS_TEST = 0x0000_0002;

    internal const int DISP_CHANGE_SUCCESSFUL = 0;
    internal const int DISP_CHANGE_RESTART = 1;
    internal const int DISP_CHANGE_FAILED = -1;
    internal const int DISP_CHANGE_BADMODE = -2;
    internal const int DISP_CHANGE_BADFLAGS = -4;
    internal const int DISP_CHANGE_BADPARAM = -5;

    /// <summary>
    /// DEVMODEW with the display-relevant fields at their canonical offsets
    /// (the printer fields share the unions). <see cref="Size"/> must be set
    /// to the struct size (220) before any call.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        internal const ushort StructSize = 220;

        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(76)] public int PositionX;
        [FieldOffset(80)] public int PositionY;
        [FieldOffset(84)] public uint DisplayOrientation;
        [FieldOffset(88)] public uint DisplayFixedOutput;
        [FieldOffset(168)] public uint BitsPerPel;
        [FieldOffset(172)] public uint PelsWidth;
        [FieldOffset(176)] public uint PelsHeight;
        [FieldOffset(180)] public uint DisplayFlags;
        [FieldOffset(184)] public uint DisplayFrequency;

        public static DevMode Create() => new() { Size = StructSize };
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW",
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplaySettingsEx(
        string deviceName, int modeNum, ref DevMode devMode, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ChangeDisplaySettingsEx(
        string deviceName, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr lParam);
}
