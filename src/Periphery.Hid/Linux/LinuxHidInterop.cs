// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Periphery.Hid.Linux;

/// <summary>
/// P/Invoke declarations for the Linux HID backend: <c>libc.so.6</c> file
/// I/O, <c>poll(2)</c>, <c>eventfd(2)</c>, and the <c>hidraw</c> ioctl
/// surface. All declarations use <see cref="LibraryImportAttribute"/> for
/// AOT/trim safety, mirroring <c>Periphery.Linux.UdevInterop</c>.
/// </summary>
/// <remarks>
/// The versioned soname (<c>libc.so.6</c>) matches the core provider's
/// convention (<c>libudev.so.1</c>) and resolves on every glibc distro
/// without requiring dev packages. musl-based distros (Alpine) are out of
/// scope for now — same posture as the core Linux provider.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class LinuxHidInterop
{
    private const string LibC = "libc.so.6";

    // ── open(2) flags ──────────────────────────────────────────────────
    internal const int O_RDWR = 0x2;
    internal const int O_NONBLOCK = 0x800;
    internal const int O_CLOEXEC = 0x80000;

    // ── errno values used for exception classification ────────────────
    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int EINTR = 4;
    internal const int EIO = 5;
    internal const int ENXIO = 6;
    internal const int EAGAIN = 11;
    internal const int EACCES = 13;
    internal const int ENODEV = 19;

    // ── poll(2) events ─────────────────────────────────────────────────
    internal const short POLLIN = 0x1;
    internal const short POLLOUT = 0x4;
    internal const short POLLERR = 0x8;
    internal const short POLLHUP = 0x10;
    internal const short POLLNVAL = 0x20;

    // ── eventfd(2) flags ───────────────────────────────────────────────
    internal const int EFD_NONBLOCK = 0x800;
    internal const int EFD_CLOEXEC = 0x80000;

    // ── hidraw ioctls (linux/hidraw.h, generic _IOC layout) ────────────
    //
    // _IOC(dir, 'H', nr, size) = (dir << 30) | (size << 16) | ('H' << 8) | nr
    // with dir: 1 = write, 2 = read, 3 = read/write. The generic layout
    // holds on every architecture .NET ships for on Linux (x64, arm64, arm).

    /// <summary>_IOR('H', 0x01, int) — report descriptor size.</summary>
    internal const nuint HIDIOCGRDESCSIZE = 0x80044801;

    /// <summary>
    /// _IOR('H', 0x02, struct hidraw_report_descriptor) — descriptor bytes.
    /// The struct is { u32 size; u8 value[4096]; } = 4100 bytes.
    /// </summary>
    internal const nuint HIDIOCGRDESC = 0x90044802;

    /// <summary>Max descriptor payload, from HID_MAX_DESCRIPTOR_SIZE.</summary>
    internal const int HID_MAX_DESCRIPTOR_SIZE = 4096;

    /// <summary>_IOC(read|write, 'H', 0x06, len) — set feature report.</summary>
    internal static nuint HidIocSFeature(int length) =>
        0xC0000000 | ((nuint)(uint)length << 16) | 0x4806;

    /// <summary>_IOC(read|write, 'H', 0x07, len) — get feature report.</summary>
    internal static nuint HidIocGFeature(int length) =>
        0xC0000000 | ((nuint)(uint)length << 16) | 0x4807;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    // ── libc functions ─────────────────────────────────────────────────

    [LibraryImport(LibC, EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags);

    [LibraryImport(LibC, EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport(LibC, EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport(LibC, EntryPoint = "write", SetLastError = true)]
    internal static unsafe partial nint Write(int fd, byte* buffer, nuint count);

    [LibraryImport(LibC, EntryPoint = "poll", SetLastError = true)]
    internal static unsafe partial int Poll(PollFd* fds, nuint nfds, int timeoutMs);

    [LibraryImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, ref int arg);

    [LibraryImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    internal static unsafe partial int Ioctl(int fd, nuint request, byte* buffer);

    [LibraryImport(LibC, EntryPoint = "eventfd", SetLastError = true)]
    internal static partial int EventFd(uint initval, int flags);

    /// <summary>Signals an eventfd by writing the 8-byte counter increment.</summary>
    internal static unsafe void SignalEventFd(int fd)
    {
        ulong one = 1;
        // Best-effort: the only failure mode that matters (fd closed during
        // teardown) is benign — the waiter is gone.
        _ = Write(fd, (byte*)&one, sizeof(ulong));
    }

    /// <summary>Drains an eventfd so the next wait blocks again.</summary>
    internal static unsafe void DrainEventFd(int fd)
    {
        ulong counter;
        _ = Read(fd, (byte*)&counter, sizeof(ulong));
    }
}
