// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Linux;

/// <summary>
/// P/Invoke declarations for <c>libudev.so.1</c>.
/// All declarations use <see cref="LibraryImportAttribute"/> for AOT/trim safety.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class UdevInterop
{
    private const string LibUdev = "libudev.so.1";

    // ── udev context ───────────────────────────────────────────────────

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_new();

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_unref(IntPtr udev);

    // ── udev_enumerate ─────────────────────────────────────────────────

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_enumerate_new(IntPtr udev);

    [LibraryImport(LibUdev)]
    internal static partial int udev_enumerate_add_match_subsystem(IntPtr enumerate, IntPtr subsystem);

    [LibraryImport(LibUdev)]
    internal static partial int udev_enumerate_scan_devices(IntPtr enumerate);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_enumerate_get_list_entry(IntPtr enumerate);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_enumerate_unref(IntPtr enumerate);

    // ── udev_list_entry ────────────────────────────────────────────────

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_list_entry_get_next(IntPtr listEntry);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_list_entry_get_name(IntPtr listEntry);

    // ── udev_device ────────────────────────────────────────────────────

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_new_from_syspath(IntPtr udev, IntPtr syspath);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_get_syspath(IntPtr device);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_get_subsystem(IntPtr device);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_get_action(IntPtr device);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_get_property_value(IntPtr device, IntPtr key);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_get_sysattr_value(IntPtr device, IntPtr sysattr);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_get_parent(IntPtr device);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_device_unref(IntPtr device);

    // ── udev_monitor ───────────────────────────────────────────────────

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_monitor_new_from_netlink(IntPtr udev, IntPtr name);

    [LibraryImport(LibUdev)]
    internal static partial int udev_monitor_filter_add_match_subsystem_devtype(
        IntPtr monitor, IntPtr subsystem, IntPtr devtype);

    [LibraryImport(LibUdev)]
    internal static partial int udev_monitor_enable_receiving(IntPtr monitor);

    [LibraryImport(LibUdev)]
    internal static partial int udev_monitor_get_fd(IntPtr monitor);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_monitor_receive_device(IntPtr monitor);

    [LibraryImport(LibUdev)]
    internal static partial IntPtr udev_monitor_unref(IntPtr monitor);

    // ── Managed string helpers ─────────────────────────────────────────
    // libudev returns const char* pointers owned by the library — they must
    // NOT be freed by the caller. We marshal them manually via PtrToStringUTF8
    // to avoid the runtime's default marshalling which could free them.

    internal static string? PtrToString(IntPtr ptr)
        => ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    internal static IntPtr StringToHGlobal(string s)
        => Marshal.StringToHGlobalAnsi(s);

    /// <summary>
    /// Reads a udev property value as a managed string.
    /// </summary>
    internal static string? GetPropertyValue(IntPtr device, string key)
    {
        var keyPtr = Marshal.StringToHGlobalAnsi(key);
        try
        {
            var valuePtr = udev_device_get_property_value(device, keyPtr);
            return PtrToString(valuePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(keyPtr);
        }
    }

    /// <summary>
    /// Reads a udev sysattr value as a managed string.
    /// </summary>
    internal static string? GetSysattrValue(IntPtr device, string sysattr)
    {
        var attrPtr = Marshal.StringToHGlobalAnsi(sysattr);
        try
        {
            var valuePtr = udev_device_get_sysattr_value(device, attrPtr);
            return PtrToString(valuePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(attrPtr);
        }
    }

    /// <summary>
    /// Creates a new udev device from a syspath string.
    /// The caller must call <see cref="udev_device_unref"/> when done.
    /// </summary>
    internal static IntPtr DeviceNewFromSyspath(IntPtr udev, string syspath)
    {
        var syspathPtr = Marshal.StringToHGlobalAnsi(syspath);
        try
        {
            return udev_device_new_from_syspath(udev, syspathPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(syspathPtr);
        }
    }

    /// <summary>
    /// Creates a new udev monitor subscribed to the <c>"udev"</c> event source
    /// (rule-processed events, not raw kernel events).
    /// </summary>
    internal static IntPtr MonitorNewFromNetlink(IntPtr udev)
    {
        var namePtr = Marshal.StringToHGlobalAnsi("udev");
        try
        {
            return udev_monitor_new_from_netlink(udev, namePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    /// <summary>
    /// Adds a subsystem match filter to the monitor.
    /// </summary>
    internal static int MonitorFilterAddMatchSubsystem(IntPtr monitor, string subsystem)
    {
        var subsystemPtr = Marshal.StringToHGlobalAnsi(subsystem);
        try
        {
            return udev_monitor_filter_add_match_subsystem_devtype(monitor, subsystemPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(subsystemPtr);
        }
    }

    /// <summary>
    /// Adds a subsystem match filter to the enumerator.
    /// </summary>
    internal static int EnumerateAddMatchSubsystem(IntPtr enumerate, string subsystem)
    {
        var subsystemPtr = Marshal.StringToHGlobalAnsi(subsystem);
        try
        {
            return udev_enumerate_add_match_subsystem(enumerate, subsystemPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(subsystemPtr);
        }
    }

    // ── poll(2) for fd readability ─────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(ref PollFd fds, uint nfds, int timeout);

    private const short POLLIN = 0x0001;

    /// <summary>
    /// Polls a file descriptor for readability with the given timeout in milliseconds.
    /// Returns <c>true</c> if data is available for reading.
    /// </summary>
    internal static bool PollFdReadable(int fd, int timeoutMs)
    {
        var pfd = new PollFd { fd = fd, events = POLLIN };
        int result = poll(ref pfd, 1, timeoutMs);
        return result > 0 && (pfd.revents & POLLIN) != 0;
    }
}
