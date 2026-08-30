// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Periphery.Windows;

/// <summary>
/// Core P/Invoke infrastructure for Windows device enumeration using
/// SetupAPI (setupapi.dll) and Configuration Manager (cfgmgr32.dll).
/// Provides device enumeration, typed property retrieval, connection-state
/// checks, and change notification registration.
/// </summary>
internal static unsafe partial class DevNodeHelper
{
    // ── Return codes ───────────────────────────────────────────────────
    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x0000001A;

    // ── CM_Locate_DevNode flags ────────────────────────────────────────
    private const int CM_LOCATE_DEVNODE_NORMAL  = 0x00000000;
    private const int CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;

    // ── Device node status flags (from cfg.h / cfgmgr32.h) ────────────
    /// <summary>Device driver is started/running.</summary>
    public const int DN_STARTED              = 0x00000008;

    /// <summary>Device is physically disconnected (Win 8+).</summary>
    public const int DN_DEVICE_DISCONNECTED  = 0x02000000;

    // ── SetupAPI flags ─────────────────────────────────────────────────
    private const uint DIGCF_PRESENT    = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    // ── DEVPROPTYPE constants ──────────────────────────────────────────
    private const uint DEVPROP_TYPE_UINT32      = 0x00000007;
    private const uint DEVPROP_TYPE_STRING      = 0x00000012;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;
    private const uint DEVPROP_TYPE_GUID        = 0x0000000D;

    // ── CM_NOTIFY constants ────────────────────────────────────────────
    internal const int CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE         = 0;
    internal const int CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE          = 2;
    internal const int CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES    = 0x00000002;
    internal const int CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL       = 0;
    internal const int CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL       = 1;
    internal const int CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED     = 7;
    internal const int CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED        = 8;
    internal const int CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED        = 9;

    private static readonly nint INVALID_HANDLE_VALUE = -1;

    // ── Structures ─────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// Notification filter for <see cref="CM_Register_Notification"/>.
    /// Size must be 416 bytes to accommodate the largest union variant
    /// (<c>DeviceInstance.InstanceId[MAX_DEVICE_ID_LEN]</c> = 400 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)]  public int cbSize;
        [FieldOffset(4)]  public int Flags;
        [FieldOffset(8)]  public int FilterType;
        [FieldOffset(12)] public int Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }



    // ── Well-known DEVPROPKEY definitions ──────────────────────────────
    // From devpkey.h — {a45c254e-df1c-4efd-8020-67d146a850e0} is the
    // Device property set used for most standard device properties.

    private static readonly Guid s_devPropDevice =
        new("a45c254e-df1c-4efd-8020-67d146a850e0");

    internal static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new()
        { fmtid = s_devPropDevice, pid = 2 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds = new()
        { fmtid = s_devPropDevice, pid = 3 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_CompatibleIds = new()
        { fmtid = s_devPropDevice, pid = 4 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_Service = new()
        { fmtid = s_devPropDevice, pid = 6 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_Class = new()
        { fmtid = s_devPropDevice, pid = 9 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_ClassGuid = new()
        { fmtid = s_devPropDevice, pid = 10 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_Manufacturer = new()
        { fmtid = s_devPropDevice, pid = 13 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new()
        { fmtid = s_devPropDevice, pid = 14 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_Capabilities = new()
        { fmtid = s_devPropDevice, pid = 17 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_BusTypeGuid = new()
        { fmtid = s_devPropDevice, pid = 23 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_Address = new()
        { fmtid = s_devPropDevice, pid = 30 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_LocationPaths = new()
        { fmtid = s_devPropDevice, pid = 37 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new()
        { fmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };

    // GUARDRAIL: this GUID/pid is validated ONLY on real hardware. A wrong constant does not fail
    // loudly — CM_Get_DevNode_Property returns CR_NO_SUCH_VALUE and the getter returns null, so the
    // property silently reads as "absent" and callers fall back. A one-byte typo here (a5a8 vs the
    // correct a5a7) once made the LocationPath parent-walk read a null parent → ByLocationPath never
    // correlated → every concurrent EFM8 flash timed out, and no unit test could catch it because the
    // tests inject a fake lookup delegate. If you add or edit a DEVPKEY, copy the fmtid+pid from the
    // Windows SDK's devpkey.h verbatim and verify on hardware — a green unit suite proves nothing here.
    internal static readonly DEVPROPKEY DEVPKEY_Device_Parent = new()
        { fmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 8 };

    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = new()
        { fmtid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 3 };

    // ── Native imports ─────────────────────────────────────────────────

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNode(out int pdnDevInst, string pDeviceID, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, int dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial int CM_Get_DevNode_Property(
        int dnDevInst,
        in DEVPROPKEY propertyKey,
        out uint propertyType,
        nint propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static partial int CM_Get_Device_ID_Size(out int pulLen, int dnDevInst, int ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial int CM_Get_Device_ID(int dnDevInst, nint buffer, int bufferLen, int ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    internal static unsafe partial int CM_Register_Notification(
        ref CM_NOTIFY_FILTER pFilter,
        nint pContext,
        delegate* unmanaged[Stdcall]<nint, nint, int, nint, int, int> pCallback,
        out nint pNotifyContext);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial int CM_Unregister_Notification(nint notifyContext);

    /// <summary>
    /// Retrieves a typed property from a device interface path.
    /// Available since Windows 8; returns CR_FAILURE on earlier versions.
    /// </summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_Property(
        string   pszDeviceInterface,
        in DEVPROPKEY PropertyKey,
        out uint PropertyType,
        nint     PropertyBuffer,
        ref uint PropertyBufferSize,
        uint     ulFlags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW")]
    private static unsafe partial nint SetupDiGetClassDevs(
        Guid* classGuid,
        nint enumerator,
        nint hwndParent,
        uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the raw device-node status bitmask for <paramref name="deviceId"/>,
    /// or <c>null</c> if the device instance cannot be located.
    /// </summary>
    public static int? GetDevNodeStatus(string deviceId)
    {
        int result = CM_Locate_DevNode(out int devInst, deviceId, CM_LOCATE_DEVNODE_PHANTOM);
        return result == CR_SUCCESS ? GetDevNodeStatus(devInst) : null;
    }

    /// <summary>
    /// Returns the raw device-node status bitmask, or <c>null</c> if the status
    /// cannot be retrieved. Avoids a redundant <c>CM_Locate_DevNode</c> call when
    /// the caller already holds a <see cref="SP_DEVINFO_DATA.DevInst"/>.
    /// </summary>
    internal static int? GetDevNodeStatus(int devInst)
    {
        int result = CM_Get_DevNode_Status(out int status, out _, devInst, 0);
        return result == CR_SUCCESS ? status : null;
    }

    /// <summary>
    /// Returns <c>true</c> if the device is started and not flagged as
    /// disconnected — i.e. it is physically connected and working.
    /// </summary>
    public static bool IsDeviceConnected(string deviceId)
    {
        int result = CM_Locate_DevNode(out int devInst, deviceId, CM_LOCATE_DEVNODE_PHANTOM);
        return result == CR_SUCCESS && IsDeviceConnected(devInst);
    }

    /// <summary>
    /// Returns the device-node problem code for <paramref name="deviceId"/>,
    /// or <c>null</c> if the device cannot be located.
    /// </summary>
    public static int? GetProblemCode(string deviceId)
    {
        int result = CM_Locate_DevNode(out int devInst, deviceId, CM_LOCATE_DEVNODE_PHANTOM);
        return result == CR_SUCCESS ? GetProblemCode(devInst) : null;
    }

    /// <summary>
    /// Locates a device node by instance ID, returning the devnode handle
    /// or <c>null</c> if the device is not found.
    /// </summary>
    internal static int? LocateDevNode(string instanceId)
    {
        int result = CM_Locate_DevNode(out int devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM);
        return result == CR_SUCCESS ? devInst : null;
    }

    /// <summary>
    /// Returns <c>true</c> if the device node is started and not flagged as disconnected.
    /// Avoids a redundant <c>CM_Locate_DevNode</c> call when the caller already holds a
    /// <see cref="SP_DEVINFO_DATA.DevInst"/>.
    /// </summary>
    internal static bool IsDeviceConnected(int devInst)
    {
        int result = CM_Get_DevNode_Status(out int status, out _, devInst, 0);
        if (result != CR_SUCCESS) return false;
        return (status & DN_STARTED) != 0 && (status & DN_DEVICE_DISCONNECTED) == 0;
    }

    /// <summary>
    /// Returns the device-node problem code, or <c>null</c> if the status cannot be
    /// retrieved. Avoids a redundant <c>CM_Locate_DevNode</c> call when the caller
    /// already holds a <see cref="SP_DEVINFO_DATA.DevInst"/>.
    /// </summary>
    internal static int? GetProblemCode(int devInst)
    {
        int result = CM_Get_DevNode_Status(out _, out int problem, devInst, 0);
        return result == CR_SUCCESS ? problem : null;
    }

    /// <summary>
    /// Resolves a device interface path (e.g. the <c>monitorDevicePath</c> from
    /// <c>DISPLAYCONFIG_TARGET_DEVICE_NAME</c>) to its canonical PnP instance ID
    /// by querying <c>DEVPKEY_Device_InstanceId</c> via cfgmgr32.
    /// Returns <c>null</c> if the interface is not found or the API is unavailable.
    /// </summary>
    internal static string? GetDeviceInterfaceInstanceId(string deviceInterfacePath)
    {
        // DEVPKEY_Device_InstanceId = {78c34fc8-104a-4aca-9ea4-524d52996e57}, pid 256
        var key = new DEVPROPKEY
        {
            fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
            pid   = 256,
        };

        uint bufSize = 0;
        CM_Get_Device_Interface_Property(deviceInterfacePath, in key, out _, nint.Zero, ref bufSize, 0);
        if (bufSize == 0)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            int result = CM_Get_Device_Interface_Property(
                deviceInterfacePath, in key, out _, buffer, ref bufSize, 0);
            return result == CR_SUCCESS ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Property retrieval helpers ─────────────────────────────────────

    /// <summary>
    /// Reads a string property from a device node using the two-call
    /// pattern (first call for buffer size, second for data).
    /// </summary>
    internal static string? GetStringProperty(int devInst, in DEVPROPKEY key)
    {
        uint size = 0;
        int result = CM_Get_DevNode_Property(devInst, in key, out _, 0, ref size, 0);
        if (result != CR_BUFFER_SMALL || size == 0)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            result = CM_Get_DevNode_Property(devInst, in key, out uint propType, buffer, ref size, 0);
            if (result != CR_SUCCESS || propType != DEVPROP_TYPE_STRING)
                return null;

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads a multi-string (REG_MULTI_SZ) property from a device node.
    /// </summary>
    internal static string[]? GetStringListProperty(int devInst, in DEVPROPKEY key)
    {
        uint size = 0;
        int result = CM_Get_DevNode_Property(devInst, in key, out _, 0, ref size, 0);
        if (result != CR_BUFFER_SMALL || size == 0)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            result = CM_Get_DevNode_Property(devInst, in key, out uint propType, buffer, ref size, 0);
            if (result != CR_SUCCESS || propType != DEVPROP_TYPE_STRING_LIST)
                return null;

            return ParseMultiString(buffer, (int)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads a <c>UINT32</c> property from a device node.
    /// </summary>
    internal static uint? GetUInt32Property(int devInst, in DEVPROPKEY key)
    {
        uint size = sizeof(uint);
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            int result = CM_Get_DevNode_Property(devInst, in key, out uint propType, buffer, ref size, 0);
            if (result != CR_SUCCESS || propType != DEVPROP_TYPE_UINT32)
                return null;

            return (uint)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads a GUID property from a device node.
    /// </summary>
    internal static Guid? GetGuidProperty(int devInst, in DEVPROPKEY key)
    {
        uint size = (uint)Marshal.SizeOf<Guid>();
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            int result = CM_Get_DevNode_Property(devInst, in key, out uint propType, buffer, ref size, 0);
            if (result != CR_SUCCESS || propType != DEVPROP_TYPE_GUID)
                return null;

            return Marshal.PtrToStructure<Guid>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the device instance ID for a given device node handle.
    /// </summary>
    internal static string? GetDeviceInstanceId(int devInst)
    {
        int result = CM_Get_Device_ID_Size(out int len, devInst, 0);
        if (result != CR_SUCCESS || len <= 0)
            return null;

        int bufferSize = (len + 1) * 2; // +1 for null terminator, ×2 for UTF-16
        nint buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = CM_Get_Device_ID(devInst, buffer, len + 1, 0);
            return result == CR_SUCCESS ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Enumeration ────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates present device instances, optionally filtered by class GUIDs.
    /// When <paramref name="classGuids"/> is <c>null</c> or empty, all device
    /// classes are enumerated.
    /// </summary>
    internal static IEnumerable<(int DevInst, string InstanceId)> EnumerateDeviceInstances(
        Guid[]? classGuids = null)
    {
        if (classGuids is null or { Length: 0 })
        {
            foreach (var item in EnumerateDeviceInfoSet(classGuid: null))
                yield return item;
        }
        else
        {
            foreach (var guid in classGuids)
            {
                foreach (var item in EnumerateDeviceInfoSet(guid))
                    yield return item;
            }
        }
    }

    private static IEnumerable<(int DevInst, string InstanceId)> EnumerateDeviceInfoSet(
        Guid? classGuid)
    {
        nint devInfoSet = OpenDeviceInfoSet(classGuid);
        if (devInfoSet == INVALID_HANDLE_VALUE)
            yield break;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

            for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref data); i++)
            {
                string? id = GetDeviceInstanceId(data.DevInst);
                if (id is not null)
                    yield return (data.DevInst, id);

                data.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    private static unsafe nint OpenDeviceInfoSet(Guid? classGuid)
    {
        if (classGuid is { } guid)
            return SetupDiGetClassDevs(&guid, 0, 0, DIGCF_PRESENT);

        return SetupDiGetClassDevs(null, 0, 0, DIGCF_PRESENT | DIGCF_ALLCLASSES);
    }

    // ── Notification helpers ───────────────────────────────────────────

    /// <summary>
    /// Reads the device instance ID from <c>CM_NOTIFY_EVENT_DATA</c>
    /// for device instance events (<c>CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE</c>).
    /// </summary>
    internal static string? ReadInstanceIdFromEventData(nint eventData, int eventDataSize)
    {
        // CM_NOTIFY_EVENT_DATA DeviceInstance layout:
        //   offset 0:  FilterType (int, 4 bytes)
        //   offset 4:  Reserved   (int, 4 bytes)
        //   offset 8:  InstanceId (null-terminated UTF-16 string)
        const int instanceIdOffset = 8;
        if (eventData == 0 || eventDataSize < instanceIdOffset + 2) return null;
        return Marshal.PtrToStringUni(eventData + instanceIdOffset);
    }

    /// <summary>
    /// Extracts a device instance ID from a device interface symbolic link.
    /// </summary>
    internal static string? ParseInstanceIdFromSymbolicLink(string symbolicLink)
    {
        // Symbolic link format:
        // \\?\USB#VID_046D&PID_C077#5&2c0e5f28&0&1#{a5dcbf10-...}
        // → Device instance: USB\VID_046D&PID_C077\5&2c0e5f28&0&1

        const string prefix = @"\\?\";
        ReadOnlySpan<char> span = symbolicLink;

        if (span.StartsWith(prefix))
            span = span[prefix.Length..];

        // Remove the interface GUID suffix (last #{guid})
        int lastHash = span.LastIndexOf('#');
        if (lastHash < 0)
            return null;

        if (lastHash + 1 < span.Length && span[lastHash + 1] == '{')
            span = span[..lastHash];

        // Replace # with backslash to get standard instance ID format
        return span.ToString().Replace('#', '\\');
    }

    /// <summary>
    /// Reads the symbolic link string from <c>CM_NOTIFY_EVENT_DATA</c>
    /// for device interface events.
    /// </summary>
    internal static string? ReadSymbolicLinkFromEventData(nint eventData, int eventDataSize)
    {
        // CM_NOTIFY_EVENT_DATA layout for DeviceInterface:
        //   offset 0:  FilterType  (int, 4 bytes)
        //   offset 4:  Reserved    (int, 4 bytes)
        //   offset 8:  ClassGuid   (Guid, 16 bytes)
        //   offset 24: SymbolicLink (null-terminated UTF-16 string)
        const int symbolicLinkOffset = 24;

        if (eventData == 0 || eventDataSize < symbolicLinkOffset + 2)
            return null;

        return Marshal.PtrToStringUni(eventData + symbolicLinkOffset);
    }

    // ── Private helpers ────────────────────────────────────────────────

    /// <summary>
    /// Parses a double-null-terminated multi-string buffer into individual strings.
    /// </summary>
    private static string[] ParseMultiString(nint buffer, int totalBytes)
    {
        var results = new List<string>();
        int offset = 0;

        while (offset < totalBytes)
        {
            // Ensure minimum space for a null terminator (2 bytes for UTF-16)
            if (offset + 2 > totalBytes)
                break;

            string? s = Marshal.PtrToStringUni(buffer + offset);
            if (string.IsNullOrEmpty(s))
                break;

            results.Add(s);
            offset += (s.Length + 1) * 2; // +1 for null terminator, ×2 for UTF-16
        }

        return results.ToArray();
    }

    /// <summary>
    /// <see cref="SafeHandle"/> wrapper for the notification context returned by
    /// <see cref="CM_Register_Notification"/>. Ensures <see cref="CM_Unregister_Notification"/>
    /// is called exactly once, even if the owning provider is not explicitly disposed.
    /// <see cref="SafeHandle"/> provides thread-safe, idempotent release.
    /// </summary>
    internal sealed class CmNotifyHandle : SafeHandle
    {
        internal CmNotifyHandle(nint handle) : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(handle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <summary>
        /// Calls <see cref="CM_Unregister_Notification"/>, which blocks until any
        /// in-progress callback has returned before unregistering.
        /// </summary>
        protected override bool ReleaseHandle() =>
            CM_Unregister_Notification(handle) == CR_SUCCESS;
    }
}
