// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Periphery.Hid.Windows;

[SupportedOSPlatform("windows")]
internal static partial class HidInterop
{
    // -----------------------------------------------------------------------
    // kernel32 — file I/O
    // -----------------------------------------------------------------------

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    // -----------------------------------------------------------------------
    // hid.dll — capabilities
    // -----------------------------------------------------------------------

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetAttributes(
        SafeFileHandle hidDeviceObject,
        ref HiddAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetPreparsedData(
        SafeFileHandle hidDeviceObject,
        out nint preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_FreePreparsedData(nint preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(nint preparsedData, ref HidpCaps caps);

    internal const int HIDP_STATUS_SUCCESS = unchecked((int)0x00110000);

    // -----------------------------------------------------------------------
    // hid.dll — feature reports (ADR-0048)
    //
    // Feature reports are the "control-plane" channel of HID. They're used
    // for status queries that don't fit the polling input-report shape
    // (battery state, configuration, calibration) and for any vendor-defined
    // device that ships its own command/response protocol over HID — most
    // notably the Megatec Q1 family of UPS clones (Cypress 0665 etc.) that
    // ride feature report 0 with ASCII payloads.
    //
    // SetLastError = true so the higher layers can surface device-locked
    // (sharing-violation) and vendor-driver-rejected errors with diagnostic
    // context rather than a generic "false."
    // -----------------------------------------------------------------------

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetFeature(
        SafeFileHandle hidDeviceObject,
        [In, Out] byte[] reportBuffer,
        uint reportBufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_SetFeature(
        SafeFileHandle hidDeviceObject,
        [In] byte[] reportBuffer,
        uint reportBufferLength);

    // -----------------------------------------------------------------------
    // cfgmgr32 — device instance ID → device interface path
    //
    // Periphery's enumeration surfaces SetupAPI device-instance IDs
    // (`HID\VID_0665&PID_5161\6&1B6066C6&0&0000`), but CreateFile needs
    // device-interface paths (`\\?\HID#...#{GUID}`). CM_Get_Device_Interface_List
    // does the mapping cleanly without the multi-handle SetupAPI dance.
    //
    // Returns CR_SUCCESS (0) on success. The list is a multi-string —
    // one or more null-terminated interface paths followed by a final
    // double-null. HID devices typically expose exactly one interface
    // per (instance ID, class GUID) pair, but the API doesn't promise it.
    // -----------------------------------------------------------------------

    /// <summary>HID class GUID — GUID_DEVINTERFACE_HID from hidclass.h.</summary>
    internal static readonly Guid GUID_DEVINTERFACE_HID =
        new("4d1e55b2-f16f-11cf-88cb-001111000030");

    internal const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    internal const int CR_SUCCESS = 0;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_Device_Interface_List_Size(
        out uint pulLen,
        in Guid interfaceClassGuid,
        string pDeviceID,
        uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_Device_Interface_List(
        in Guid interfaceClassGuid,
        string pDeviceID,
        [Out] char[] buffer,
        uint bufferLen,
        uint ulFlags);

    // -----------------------------------------------------------------------
    // Structs
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
