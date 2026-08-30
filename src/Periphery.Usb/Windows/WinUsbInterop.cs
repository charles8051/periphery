// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Periphery.Usb.Windows;

/// <summary>
/// <c>[LibraryImport]</c> surface for the Windows raw-USB path: <c>kernel32</c>
/// <c>CreateFile</c> / <c>CancelIoEx</c>, <c>cfgmgr32</c> device-interface-path
/// resolution, and the <c>winusb.dll</c> claim + transfer functions. Mirrors the
/// conventions in <c>Periphery.Hid.Windows.HidInterop</c> (source-generated
/// marshalling, <c>SetLastError</c>, <c>nint</c> handles).
/// </summary>
/// <remarks>
/// The transfer functions (<see cref="WinUsb_ReadPipe"/>, <see cref="WinUsb_WritePipe"/>,
/// <see cref="WinUsb_ControlTransfer"/>) take the data buffer as a raw <c>nint</c>
/// pointer, not a managed array, because they are issued <b>overlapped</b>: the
/// caller pins the buffer for the lifetime of the async operation (via
/// <c>ThreadPoolBoundHandle.AllocateNativeOverlapped</c>) and passes its pinned
/// address. Letting the source-gen marshaller pin/copy a <c>byte[]</c> per call
/// would unpin it the moment the call returns <c>ERROR_IO_PENDING</c>, while the
/// kernel is still writing into it. The synchronous descriptor functions keep
/// managed-array marshalling.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WinUsbInterop
{
    // ── Win32 status codes ─────────────────────────────────────────────
    internal const int ERROR_IO_PENDING = 997;
    internal const int ERROR_OPERATION_ABORTED = 995;

    // -----------------------------------------------------------------------
    // kernel32 — open the device-interface handle + cancel in-flight I/O
    // -----------------------------------------------------------------------

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    /// <summary>Required for WinUSB: the device handle must be opened overlapped.</summary>
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

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

    /// <summary>Cancels a specific in-flight overlapped operation on a file handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeFileHandle hFile, nint lpOverlapped);

    // -----------------------------------------------------------------------
    // cfgmgr32 — resolve a device-instance ID to a device-interface path
    // -----------------------------------------------------------------------

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE (usbiodef.h). Treehopper and most
    /// WinUSB-bound devices expose this interface class.</summary>
    internal static readonly Guid GUID_DEVINTERFACE_USB_DEVICE =
        new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

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
    // winusb.dll — claim + descriptors (synchronous, used at open)
    // -----------------------------------------------------------------------

    internal const byte USB_DEVICE_DESCRIPTOR_TYPE = 0x01;
    internal const byte USB_CONFIGURATION_DESCRIPTOR_TYPE = 0x02;

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out nint interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Free(nint interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_GetDescriptor(
        nint interfaceHandle,
        byte descriptorType,
        byte index,
        ushort languageID,
        [Out] byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryInterfaceSettings(
        nint interfaceHandle,
        byte alternateSettingNumber,
        out USB_INTERFACE_DESCRIPTOR descriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryPipe(
        nint interfaceHandle,
        byte alternateSettingNumber,
        byte pipeIndex,
        out WINUSB_PIPE_INFORMATION pipeInformation);

    // -----------------------------------------------------------------------
    // winusb.dll — transfers (issued overlapped: buffer is a pinned pointer,
    // overlapped is a NativeOverlapped*; completion arrives on the IOCP)
    // -----------------------------------------------------------------------

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ControlTransfer(
        nint interfaceHandle,
        WINUSB_SETUP_PACKET setupPacket,
        nint buffer,
        uint bufferLength,
        out uint lengthTransferred,
        nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ReadPipe(
        nint interfaceHandle,
        byte pipeID,
        nint buffer,
        uint bufferLength,
        out uint lengthTransferred,
        nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_WritePipe(
        nint interfaceHandle,
        byte pipeID,
        nint buffer,
        uint bufferLength,
        out uint lengthTransferred,
        nint overlapped);

    // -----------------------------------------------------------------------
    // Structs (all blittable — no custom marshalling needed)
    // -----------------------------------------------------------------------

    /// <summary>The 8-byte USB SETUP packet, passed by value to WinUsb_ControlTransfer.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    /// <summary>USB_INTERFACE_DESCRIPTOR (usbspec.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    /// <summary>WINUSB_PIPE_INFORMATION (winusbio.h). <c>PipeType</c> is a
    /// USBD_PIPE_TYPE enum: 0=Control, 1=Isochronous, 2=Bulk, 3=Interrupt.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WINUSB_PIPE_INFORMATION
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }
}
