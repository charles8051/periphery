// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Periphery.Usb.Linux;

/// <summary>
/// P/Invoke declarations for <c>libusb-1.0.so.0</c> plus the few
/// <c>libc.so.6</c> calls needed to open the usbfs node. All declarations use
/// <see cref="LibraryImportAttribute"/> for AOT/trim safety, mirroring the
/// core provider's <c>UdevInterop</c> convention.
/// </summary>
/// <remarks>
/// The device is opened via <c>libusb_wrap_sys_device</c> (libusb ≥ 1.0.23,
/// 2019) around an fd Periphery opens itself on
/// <c>/dev/bus/usb/BBB/DDD</c> — no device-list walk, no enumeration race,
/// and open-permission errors surface as plain <c>errno</c> values that map
/// cleanly onto the <see cref="UsbException"/> hierarchy (ADR-0038).
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class LibUsbInterop
{
    private const string LibUsb = "libusb-1.0.so.0";
    private const string LibC = "libc.so.6";

    // ── libusb error codes ─────────────────────────────────────────────
    internal const int LIBUSB_SUCCESS = 0;
    internal const int LIBUSB_ERROR_IO = -1;
    internal const int LIBUSB_ERROR_INVALID_PARAM = -2;
    internal const int LIBUSB_ERROR_ACCESS = -3;
    internal const int LIBUSB_ERROR_NO_DEVICE = -4;
    internal const int LIBUSB_ERROR_NOT_FOUND = -5;
    internal const int LIBUSB_ERROR_BUSY = -6;
    internal const int LIBUSB_ERROR_TIMEOUT = -7;
    internal const int LIBUSB_ERROR_OVERFLOW = -8;
    internal const int LIBUSB_ERROR_PIPE = -9;
    internal const int LIBUSB_ERROR_INTERRUPTED = -10;
    internal const int LIBUSB_ERROR_NO_MEM = -11;
    internal const int LIBUSB_ERROR_NOT_SUPPORTED = -12;
    internal const int LIBUSB_ERROR_OTHER = -99;

    // ── libusb transfer status ─────────────────────────────────────────
    internal const int LIBUSB_TRANSFER_COMPLETED = 0;
    internal const int LIBUSB_TRANSFER_ERROR = 1;
    internal const int LIBUSB_TRANSFER_TIMED_OUT = 2;
    internal const int LIBUSB_TRANSFER_CANCELLED = 3;
    internal const int LIBUSB_TRANSFER_STALL = 4;
    internal const int LIBUSB_TRANSFER_NO_DEVICE = 5;
    internal const int LIBUSB_TRANSFER_OVERFLOW = 6;

    // ── libusb transfer types ──────────────────────────────────────────
    internal const byte LIBUSB_TRANSFER_TYPE_CONTROL = 0;
    internal const byte LIBUSB_TRANSFER_TYPE_BULK = 2;
    internal const byte LIBUSB_TRANSFER_TYPE_INTERRUPT = 3;

    internal const int LIBUSB_CONTROL_SETUP_SIZE = 8;

    // ── open(2) flags / errno (usbfs node) ─────────────────────────────
    internal const int O_RDWR = 0x2;
    internal const int O_CLOEXEC = 0x80000;
    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int ENXIO = 6;
    internal const int EACCES = 13;
    internal const int ENODEV = 19;

    /// <summary>
    /// Native layout of <c>struct libusb_device_descriptor</c> (18 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public ushort BcdUsb;
        public byte DeviceClass;
        public byte DeviceSubClass;
        public byte DeviceProtocol;
        public byte MaxPacketSize0;
        public ushort IdVendor;
        public ushort IdProduct;
        public ushort BcdDevice;
        public byte IManufacturer;
        public byte IProduct;
        public byte ISerialNumber;
        public byte NumConfigurations;
    }

    /// <summary>Native layout of <c>struct libusb_endpoint_descriptor</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EndpointDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte EndpointAddress;
        public byte BmAttributes;
        public ushort MaxPacketSize;
        public byte Interval;
        public byte Refresh;
        public byte SynchAddress;
        public IntPtr Extra;
        public int ExtraLength;
    }

    /// <summary>Native layout of <c>struct libusb_interface_descriptor</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte IInterface;
        public IntPtr Endpoint;       // EndpointDescriptor[NumEndpoints]
        public IntPtr Extra;
        public int ExtraLength;
    }

    /// <summary>Native layout of <c>struct libusb_interface</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Interface
    {
        public IntPtr AltSetting;     // InterfaceDescriptor[NumAltSetting]
        public int NumAltSetting;
    }

    /// <summary>Native layout of <c>struct libusb_config_descriptor</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ConfigDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public ushort TotalLength;
        public byte NumInterfaces;
        public byte ConfigurationValue;
        public byte IConfiguration;
        public byte BmAttributes;
        public byte MaxPower;         // Units of 2 mA (high-speed); ×2 for mA.
        public IntPtr Interfaces;     // Interface[NumInterfaces]
        public IntPtr Extra;
        public int ExtraLength;
    }

    /// <summary>
    /// Native layout of <c>struct libusb_transfer</c> (without the trailing
    /// flexible iso-packet array, which control/bulk/interrupt never use).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Transfer
    {
        public IntPtr DevHandle;
        public byte Flags;
        public byte Endpoint;
        public byte Type;
        public uint Timeout;
        public int Status;
        public int Length;
        public int ActualLength;
        public IntPtr Callback;       // void (*)(struct libusb_transfer*)
        public IntPtr UserData;
        public IntPtr Buffer;
        public int NumIsoPackets;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Timeval
    {
        public nint Seconds;
        public nint Microseconds;
    }

    // ── Context ────────────────────────────────────────────────────────

    [LibraryImport(LibUsb, EntryPoint = "libusb_init")]
    internal static partial int Init(out IntPtr context);

    [LibraryImport(LibUsb, EntryPoint = "libusb_exit")]
    internal static partial void Exit(IntPtr context);

    // ── Open / close ───────────────────────────────────────────────────

    [LibraryImport(LibUsb, EntryPoint = "libusb_wrap_sys_device")]
    internal static partial int WrapSysDevice(IntPtr context, IntPtr sysDevFd, out IntPtr deviceHandle);

    [LibraryImport(LibUsb, EntryPoint = "libusb_close")]
    internal static partial void Close(IntPtr deviceHandle);

    [LibraryImport(LibUsb, EntryPoint = "libusb_get_device")]
    internal static partial IntPtr GetDevice(IntPtr deviceHandle);

    [LibraryImport(LibUsb, EntryPoint = "libusb_set_auto_detach_kernel_driver")]
    internal static partial int SetAutoDetachKernelDriver(IntPtr deviceHandle, int enable);

    // ── Descriptors ────────────────────────────────────────────────────

    [LibraryImport(LibUsb, EntryPoint = "libusb_get_device_descriptor")]
    internal static partial int GetDeviceDescriptor(IntPtr device, out DeviceDescriptor descriptor);

    [LibraryImport(LibUsb, EntryPoint = "libusb_get_active_config_descriptor")]
    internal static partial int GetActiveConfigDescriptor(IntPtr device, out IntPtr config);

    [LibraryImport(LibUsb, EntryPoint = "libusb_get_config_descriptor")]
    internal static partial int GetConfigDescriptor(IntPtr device, byte configIndex, out IntPtr config);

    [LibraryImport(LibUsb, EntryPoint = "libusb_free_config_descriptor")]
    internal static partial void FreeConfigDescriptor(IntPtr config);

    // ── Interface claim ────────────────────────────────────────────────

    [LibraryImport(LibUsb, EntryPoint = "libusb_claim_interface")]
    internal static partial int ClaimInterface(IntPtr deviceHandle, int interfaceNumber);

    [LibraryImport(LibUsb, EntryPoint = "libusb_release_interface")]
    internal static partial int ReleaseInterface(IntPtr deviceHandle, int interfaceNumber);

    // ── Asynchronous transfers ─────────────────────────────────────────

    [LibraryImport(LibUsb, EntryPoint = "libusb_alloc_transfer")]
    internal static partial IntPtr AllocTransfer(int isoPackets);

    [LibraryImport(LibUsb, EntryPoint = "libusb_free_transfer")]
    internal static partial void FreeTransfer(IntPtr transfer);

    [LibraryImport(LibUsb, EntryPoint = "libusb_submit_transfer")]
    internal static partial int SubmitTransfer(IntPtr transfer);

    [LibraryImport(LibUsb, EntryPoint = "libusb_cancel_transfer")]
    internal static partial int CancelTransfer(IntPtr transfer);

    // ── Event pump ─────────────────────────────────────────────────────

    [LibraryImport(LibUsb, EntryPoint = "libusb_handle_events_timeout_completed")]
    internal static unsafe partial int HandleEventsTimeoutCompleted(
        IntPtr context, Timeval* timeout, int* completed);

    [LibraryImport(LibUsb, EntryPoint = "libusb_interrupt_event_handler")]
    internal static partial void InterruptEventHandler(IntPtr context);

    // ── libc (usbfs node) ──────────────────────────────────────────────

    [LibraryImport(LibC, EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenFd(string path, int flags);

    [LibraryImport(LibC, EntryPoint = "close", SetLastError = true)]
    internal static partial int CloseFd(int fd);

    /// <summary>Human-readable name for a libusb error code, for diagnostics.</summary>
    internal static string ErrorName(int code) => code switch
    {
        LIBUSB_ERROR_IO => "LIBUSB_ERROR_IO",
        LIBUSB_ERROR_INVALID_PARAM => "LIBUSB_ERROR_INVALID_PARAM",
        LIBUSB_ERROR_ACCESS => "LIBUSB_ERROR_ACCESS",
        LIBUSB_ERROR_NO_DEVICE => "LIBUSB_ERROR_NO_DEVICE",
        LIBUSB_ERROR_NOT_FOUND => "LIBUSB_ERROR_NOT_FOUND",
        LIBUSB_ERROR_BUSY => "LIBUSB_ERROR_BUSY",
        LIBUSB_ERROR_TIMEOUT => "LIBUSB_ERROR_TIMEOUT",
        LIBUSB_ERROR_OVERFLOW => "LIBUSB_ERROR_OVERFLOW",
        LIBUSB_ERROR_PIPE => "LIBUSB_ERROR_PIPE",
        LIBUSB_ERROR_INTERRUPTED => "LIBUSB_ERROR_INTERRUPTED",
        LIBUSB_ERROR_NO_MEM => "LIBUSB_ERROR_NO_MEM",
        LIBUSB_ERROR_NOT_SUPPORTED => "LIBUSB_ERROR_NOT_SUPPORTED",
        LIBUSB_ERROR_OTHER => "LIBUSB_ERROR_OTHER",
        _ => $"libusb error {code}",
    };
}
