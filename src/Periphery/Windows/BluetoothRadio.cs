// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Periphery.Windows;

/// <summary>
/// Win32 access to the local Bluetooth radios: listing them, opening one, and asking its
/// stack for the remote devices it knows. Shared by <see cref="WindowsBluetoothLinkWatch"/>
/// (link events, issue #286) and <see cref="WindowsDeviceProvider"/> (link state at
/// enumeration, issue #288).
///
/// <para>A radio is opened with <c>FILE_READ_ATTRIBUTES</c> and closed as soon as the call that
/// needed it returns. That is enough for both uses (<c>CM_Register_Notification</c>, and
/// <c>IOCTL_BTH_GET_DEVICE_INFO</c>, which is <c>FILE_ANY_ACCESS</c>), works unelevated, and
/// never holds anything that could block the radio being disabled or removed.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class BluetoothRadio
{
    private static readonly ILogger _logger = PeripheryLoggerFactory.CreateLogger(typeof(BluetoothRadio).FullName!);

    internal static readonly Guid GUID_BTHPORT_DEVICE_INTERFACE = new("0850302a-b344-4fda-9be9-90576b8d46f0");

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x1A;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_MORE_DATA = 234;
    internal static readonly nint INVALID_HANDLE_VALUE = -1;

    // CTL_CODE(FILE_DEVICE_BLUETOOTH 0x41, 0x02, METHOD_BUFFERED, FILE_ANY_ACCESS), bthioctl.h.
    private const uint IOCTL_BTH_GET_DEVICE_INFO = 0x00410008;

    /// <summary>The interface paths of every present radio.</summary>
    internal static List<string> InterfacePaths()
    {
        var interfaceClass = GUID_BTHPORT_DEVICE_INTERFACE;
        // The list can grow between the size query and the read.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, 0) != CR_SUCCESS)
                return [];

            var buffer = new char[length];
            int cr;
            fixed (char* p = buffer)
                cr = CM_Get_Device_Interface_ListW(ref interfaceClass, null, p, length, 0);

            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) return [];
            return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        return [];
    }

    /// <summary>
    /// Opens a radio for a notification registration or a query. Returns
    /// <see cref="INVALID_HANDLE_VALUE"/> on failure; the caller closes it with <see cref="Close"/>.
    /// </summary>
    internal static nint Open(string interfacePath) =>
        CreateFileW(interfacePath, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);

    internal static void Close(nint radio) => CloseHandle(radio);

    /// <summary>
    /// Address → <c>BDIF_*</c> flags for every remote device any radio's stack knows, merged
    /// across radios. Returns <see langword="null"/> when there is no radio or none answered,
    /// so the caller keeps the devnode's own status.
    /// </summary>
    internal static Dictionary<ulong, uint>? TryReadStackFlags()
    {
        Dictionary<ulong, uint>? merged = null;
        foreach (string path in InterfacePaths())
        {
            nint radio = Open(path);
            if (radio == INVALID_HANDLE_VALUE)
            {
                _logger.LogDebug("Could not open a Bluetooth radio to read link state (Win32 error {Error}).", Marshal.GetLastPInvokeError());
                continue;
            }

            Dictionary<ulong, uint>? flags;
            try
            {
                flags = ReadDeviceList(radio);
            }
            finally
            {
                Close(radio);
            }

            if (flags is null) continue;
            merged ??= [];
            foreach (var (address, bits) in flags)
                merged[address] = merged.GetValueOrDefault(address) | bits;
        }
        return merged;
    }

    private static Dictionary<ulong, uint>? ReadDeviceList(nint radio)
    {
        int capacity = 16;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int size = BluetoothStackLinkState.ListHeaderSize + capacity * BluetoothStackLinkState.DeviceInfoSize;
            var buffer = new byte[size];
            // numOfDevices is [IN/OUT]: the capacity going in, the device count coming out.
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)capacity);

            bool ok;
            uint returned;
            fixed (byte* p = buffer)
                ok = DeviceIoControl(radio, IOCTL_BTH_GET_DEVICE_INFO, p, (uint)size, p, (uint)size, out returned, 0);

            if (!ok)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is ERROR_MORE_DATA or ERROR_INSUFFICIENT_BUFFER)
                {
                    capacity *= 4;
                    continue;
                }
                _logger.LogDebug("IOCTL_BTH_GET_DEVICE_INFO failed (Win32 error {Error}).", error);
                return null;
            }

            uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (count > capacity)
            {
                capacity = (int)Math.Min(count, 4096);
                continue;
            }

            var flags = BluetoothStackLinkState.ParseDeviceInfoList(buffer.AsSpan(0, (int)Math.Min(returned, (uint)size)));
            if (flags is null)
                _logger.LogDebug("IOCTL_BTH_GET_DEVICE_INFO returned {Returned} bytes, too few for {Count} device(s).", returned, count);
            return flags;
        }

        _logger.LogDebug("IOCTL_BTH_GET_DEVICE_INFO: the device list kept outgrowing the buffer.");
        return null;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_Interface_List_SizeW(
        out uint pulLen, ref Guid interfaceClassGuid, char* pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_Interface_ListW(
        ref Guid interfaceClassGuid, char* pDeviceID, char* buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        nint device, uint ioControlCode, void* inBuffer, uint inSize, void* outBuffer, uint outSize,
        out uint bytesReturned, nint overlapped);
}
