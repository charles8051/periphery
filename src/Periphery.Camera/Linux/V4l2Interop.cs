// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Periphery.Camera.Linux;

/// <summary>
/// P/Invoke declarations for the Linux V4L2 backend: <c>libc.so.6</c> file
/// I/O, <c>mmap(2)</c>, <c>poll(2)</c>, <c>eventfd(2)</c>, and the V4L2 ioctl
/// surface. All declarations use <see cref="LibraryImportAttribute"/> for
/// AOT/trim safety, mirroring the core provider's <c>UdevInterop</c>.
/// </summary>
/// <remarks>
/// Struct layouts and ioctl request numbers are the 64-bit
/// (<c>linux-x64</c> / <c>linux-arm64</c>) generic-ABI values —
/// <c>struct timeval</c> inside <c>v4l2_buffer</c> is two native words, so
/// 32-bit ARM would need different offsets. That matches this project's
/// deployment targets and the core Linux provider's posture.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class V4l2Interop
{
    private const string LibC = "libc.so.6";

    // ── open(2) flags / errno ──────────────────────────────────────────
    internal const int O_RDWR = 0x2;
    internal const int O_NONBLOCK = 0x800;
    internal const int O_CLOEXEC = 0x80000;

    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int EINTR = 4;
    internal const int EIO = 5;
    internal const int ENXIO = 6;
    internal const int EAGAIN = 11;
    internal const int EACCES = 13;
    internal const int EBUSY = 16;
    internal const int ENODEV = 19;
    internal const int EINVAL = 22;

    // ── poll(2) ────────────────────────────────────────────────────────
    internal const short POLLIN = 0x1;
    internal const short POLLERR = 0x8;
    internal const short POLLHUP = 0x10;
    internal const short POLLNVAL = 0x20;

    // ── eventfd(2) ─────────────────────────────────────────────────────
    internal const int EFD_NONBLOCK = 0x800;
    internal const int EFD_CLOEXEC = 0x80000;

    // ── mmap(2) ────────────────────────────────────────────────────────
    internal const int PROT_READ = 0x1;
    internal const int PROT_WRITE = 0x2;
    internal const int MAP_SHARED = 0x1;
    internal static readonly IntPtr MAP_FAILED = new(-1);

    // ── V4L2 ioctl requests (videodev2.h, 64-bit generic layout) ──────
    internal const nuint VIDIOC_QUERYCAP = 0x80685600;          // _IOR ('V',  0, v4l2_capability[104])
    internal const nuint VIDIOC_ENUM_FMT = 0xC0405602;          // _IOWR('V',  2, v4l2_fmtdesc[64])
    internal const nuint VIDIOC_G_FMT = 0xC0D05604;             // _IOWR('V',  4, v4l2_format[208])
    internal const nuint VIDIOC_S_FMT = 0xC0D05605;             // _IOWR('V',  5, v4l2_format[208])
    internal const nuint VIDIOC_REQBUFS = 0xC0145608;           // _IOWR('V',  8, v4l2_requestbuffers[20])
    internal const nuint VIDIOC_QUERYBUF = 0xC0585609;          // _IOWR('V',  9, v4l2_buffer[88])
    internal const nuint VIDIOC_QBUF = 0xC058560F;              // _IOWR('V', 15, v4l2_buffer[88])
    internal const nuint VIDIOC_DQBUF = 0xC0585611;             // _IOWR('V', 17, v4l2_buffer[88])
    internal const nuint VIDIOC_STREAMON = 0x40045612;          // _IOW ('V', 18, int)
    internal const nuint VIDIOC_STREAMOFF = 0x40045613;         // _IOW ('V', 19, int)
    internal const nuint VIDIOC_G_PARM = 0xC0CC5615;            // _IOWR('V', 21, v4l2_streamparm[204])
    internal const nuint VIDIOC_S_PARM = 0xC0CC5616;            // _IOWR('V', 22, v4l2_streamparm[204])
    internal const nuint VIDIOC_G_CTRL = 0xC008561B;            // _IOWR('V', 27, v4l2_control[8])
    internal const nuint VIDIOC_S_CTRL = 0xC008561C;            // _IOWR('V', 28, v4l2_control[8])
    internal const nuint VIDIOC_QUERYCTRL = 0xC0445624;         // _IOWR('V', 36, v4l2_queryctrl[68])
    internal const nuint VIDIOC_QUERYMENU = 0xC02C5625;         // _IOWR('V', 37, v4l2_querymenu[44])
    internal const nuint VIDIOC_ENUM_FRAMESIZES = 0xC02C564A;   // _IOWR('V', 74, v4l2_frmsizeenum[44])
    internal const nuint VIDIOC_ENUM_FRAMEINTERVALS = 0xC034564B; // _IOWR('V', 75, v4l2_frmivalenum[52])

    // ── V4L2 enums / flags ─────────────────────────────────────────────
    internal const uint V4L2_BUF_TYPE_VIDEO_CAPTURE = 1;
    internal const uint V4L2_MEMORY_MMAP = 1;
    internal const uint V4L2_FIELD_NONE = 1;

    internal const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    internal const uint V4L2_CAP_STREAMING = 0x04000000;
    internal const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;

    internal const uint V4L2_FRMSIZE_TYPE_DISCRETE = 1;
    internal const uint V4L2_FRMSIZE_TYPE_CONTINUOUS = 2;
    internal const uint V4L2_FRMSIZE_TYPE_STEPWISE = 3;
    internal const uint V4L2_FRMIVAL_TYPE_DISCRETE = 1;

    internal const uint V4L2_BUF_FLAG_ERROR = 0x00000040;

    internal const uint V4L2_CTRL_FLAG_DISABLED = 0x00000001;
    internal const uint V4L2_CTRL_FLAG_READ_ONLY = 0x00000004;
    internal const uint V4L2_CTRL_TYPE_INTEGER = 1;
    internal const uint V4L2_CTRL_TYPE_BOOLEAN = 2;
    internal const uint V4L2_CTRL_TYPE_MENU = 3;

    // ── Control IDs (user class 0x0098xxxx, camera class 0x009Axxxx) ──
    internal const uint V4L2_CID_BRIGHTNESS = 0x00980900;
    internal const uint V4L2_CID_CONTRAST = 0x00980901;
    internal const uint V4L2_CID_SATURATION = 0x00980902;
    internal const uint V4L2_CID_HUE = 0x00980903;
    internal const uint V4L2_CID_AUTO_WHITE_BALANCE = 0x0098090C;
    internal const uint V4L2_CID_GAMMA = 0x00980910;
    internal const uint V4L2_CID_AUTOGAIN = 0x00980912;
    internal const uint V4L2_CID_GAIN = 0x00980913;
    internal const uint V4L2_CID_POWER_LINE_FREQUENCY = 0x00980918;
    internal const uint V4L2_CID_WHITE_BALANCE_TEMPERATURE = 0x0098091A;
    internal const uint V4L2_CID_SHARPNESS = 0x0098091B;
    internal const uint V4L2_CID_BACKLIGHT_COMPENSATION = 0x0098091C;
    internal const uint V4L2_CID_EXPOSURE_AUTO = 0x009A0901;

    // v4l2_exposure_auto_type. Note the sense is NOT boolean and NOT the way
    // round a reader expects: AUTO is zero and MANUAL is one, the opposite of
    // every other auto control here (AUTOGAIN, AUTO_WHITE_BALANCE, FOCUS_AUTO
    // are booleans where 1 means automatic). Interpreting this control as a
    // boolean reports every auto-exposure camera as manual.
    internal const int V4L2_EXPOSURE_AUTO_MODE = 0;
    internal const int V4L2_EXPOSURE_MANUAL = 1;
    internal const int V4L2_EXPOSURE_SHUTTER_PRIORITY = 2;
    internal const int V4L2_EXPOSURE_APERTURE_PRIORITY = 3;
    internal const uint V4L2_CID_EXPOSURE_ABSOLUTE = 0x009A0902;
    internal const uint V4L2_CID_PAN_ABSOLUTE = 0x009A0908;
    internal const uint V4L2_CID_TILT_ABSOLUTE = 0x009A0909;
    internal const uint V4L2_CID_FOCUS_ABSOLUTE = 0x009A090A;
    internal const uint V4L2_CID_FOCUS_AUTO = 0x009A090C;
    internal const uint V4L2_CID_ZOOM_ABSOLUTE = 0x009A090D;

    // ── Structs ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct V4l2Capability
    {
        public fixed byte Driver[16];
        public fixed byte Card[32];
        public fixed byte BusInfo[32];
        public uint Version;
        public uint Capabilities;
        public uint DeviceCaps;
        public fixed uint Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct V4l2FmtDesc
    {
        public uint Index;
        public uint Type;
        public uint Flags;
        public fixed byte Description[32];
        public uint PixelFormat;
        public uint MbusCode;
        public fixed uint Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct V4l2FrmSizeEnum
    {
        public uint Index;
        public uint PixelFormat;
        public uint Type;
        // Union: discrete { width, height } / stepwise { min/max/step × w/h }.
        public uint MinWidth;       // discrete.width
        public uint MaxWidth;
        public uint StepWidth;
        public uint MinHeight;      // discrete.height when Type == DISCRETE
        public uint MaxHeight;
        public uint StepHeight;
        public uint Reserved0;
        public uint Reserved1;

        public readonly uint DiscreteWidth => MinWidth;
        public readonly uint DiscreteHeight => MaxWidth; // union: discrete.height is the second u32
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct V4l2FrmIvalEnum
    {
        public uint Index;
        public uint PixelFormat;
        public uint Width;
        public uint Height;
        public uint Type;
        // Union: discrete v4l2_fract / stepwise { min, max, step } fracts.
        public uint Numerator;      // discrete.numerator
        public uint Denominator;    // discrete.denominator
        public uint MaxNumerator;
        public uint MaxDenominator;
        public uint StepNumerator;
        public uint StepDenominator;
        public uint Reserved0;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct V4l2PixFormat
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public uint Field;
        public uint BytesPerLine;
        public uint SizeImage;
        public uint Colorspace;
        public uint Priv;
        public uint Flags;
        public uint YcbcrEnc;
        public uint Quantization;
        public uint XferFunc;
    }

    /// <summary>
    /// <c>struct v4l2_format</c>: type + a 200-byte union 8-aligned because
    /// some union members carry pointers. Only the single-planar
    /// <see cref="V4l2PixFormat"/> view is used.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 208)]
    internal struct V4l2Format
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public V4l2PixFormat Pix;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct V4l2RequestBuffers
    {
        public uint Count;
        public uint Type;
        public uint Memory;
        public uint Capabilities;
        public fixed byte Reserved[4];
    }

    /// <summary>
    /// <c>struct v4l2_buffer</c> on 64-bit: the embedded <c>timeval</c> is
    /// two native words at offset 24 (8-aligned), the m union is one word at
    /// offset 64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    internal struct V4l2Buffer
    {
        [FieldOffset(0)] public uint Index;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint BytesUsed;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Field;
        [FieldOffset(24)] public nint TimestampSeconds;
        [FieldOffset(32)] public nint TimestampMicroseconds;
        // v4l2_timecode @40..55 — unused.
        [FieldOffset(56)] public uint Sequence;
        [FieldOffset(60)] public uint Memory;
        [FieldOffset(64)] public uint MmapOffset; // m union: __u32 offset for V4L2_MEMORY_MMAP.
        [FieldOffset(72)] public uint Length;
    }

    /// <summary>
    /// <c>struct v4l2_querymenu</c>. Asks whether one entry of a menu control exists.
    /// </summary>
    /// <remarks>
    /// PACKED, unlike <see cref="V4l2QueryCtrl"/> — the kernel declares this one
    /// <c>__attribute__((packed))</c>, and the trailing <c>reserved</c> would otherwise be
    /// padded to an 8-byte boundary by the <c>__s64</c> arm of its union and the struct would
    /// be 48 bytes instead of 44. The ioctl request code encodes the size, so getting this
    /// wrong is an immediate <c>EINVAL</c> rather than a subtle misread.
    /// <para>
    /// <c>Name</c> is the union: <c>__u8 name[32]</c> for a plain menu, <c>__s64 value</c> for
    /// an integer menu. Only the ioctl's success matters here — "does the device advertise this
    /// entry?" — so the payload is never decoded.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct V4l2QueryMenu
    {
        public uint Id;
        public uint Index;
        public fixed byte Name[32];
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct V4l2QueryCtrl
    {
        public uint Id;
        public uint Type;
        public fixed byte Name[32];
        public int Minimum;
        public int Maximum;
        public int Step;
        public int DefaultValue;
        public uint Flags;
        public fixed uint Reserved[2];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct V4l2Control
    {
        public uint Id;
        public int Value;
    }

    /// <summary>
    /// <c>struct v4l2_streamparm</c>'s capture view:
    /// type + v4l2_captureparm, padded to the full 204-byte union size.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 204)]
    internal struct V4l2StreamParm
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Capability;
        [FieldOffset(8)] public uint CaptureMode;
        [FieldOffset(12)] public uint TimePerFrameNumerator;
        [FieldOffset(16)] public uint TimePerFrameDenominator;
        [FieldOffset(20)] public uint ExtendedMode;
        [FieldOffset(24)] public uint ReadBuffers;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    internal const uint V4L2_CAP_TIMEPERFRAME = 0x1000;

    // ── libc functions ─────────────────────────────────────────────────

    [LibraryImport(LibC, EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags);

    [LibraryImport(LibC, EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    // SafeHandle rather than int, so the runtime ref-counts the descriptor for the duration
    // of the call: teardown cannot close it underneath an in-flight ioctl, and a call that
    // arrives after teardown throws instead of addressing a recycled fd (#256).
    [LibraryImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    internal static unsafe partial int Ioctl(V4l2FileDescriptor fd, nuint request, void* arg);

    [LibraryImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(V4l2FileDescriptor fd, nuint request, ref int arg);

    [LibraryImport(LibC, EntryPoint = "mmap", SetLastError = true)]
    internal static partial IntPtr Mmap(
        IntPtr addr, nuint length, int prot, int flags, V4l2FileDescriptor fd, nint offset);

    [LibraryImport(LibC, EntryPoint = "munmap", SetLastError = true)]
    internal static partial int Munmap(IntPtr addr, nuint length);

    [LibraryImport(LibC, EntryPoint = "poll", SetLastError = true)]
    internal static unsafe partial int Poll(PollFd* fds, nuint nfds, int timeoutMs);

    [LibraryImport(LibC, EntryPoint = "eventfd", SetLastError = true)]
    internal static partial int EventFd(uint initval, int flags);

    // Handle-typed for the same reason as ioctl: the wake eventfd is read by the capture
    // thread and closed by teardown, so a bare int here has exactly the recycled-descriptor
    // race this change removes for the device fd (#273 review turn 1).
    [LibraryImport(LibC, EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial nint ReadFd(V4l2FileDescriptor fd, byte* buffer, nuint count);

    [LibraryImport(LibC, EntryPoint = "write", SetLastError = true)]
    internal static unsafe partial nint WriteFd(V4l2FileDescriptor fd, byte* buffer, nuint count);

    /// <summary>Signals an eventfd by writing the 8-byte counter increment.</summary>
    internal static unsafe void SignalEventFd(V4l2FileDescriptor fd)
    {
        ulong one = 1;
        _ = WriteFd(fd, (byte*)&one, sizeof(ulong));
    }

    /// <summary>Drains an eventfd so the next wait blocks again.</summary>
    internal static unsafe void DrainEventFd(V4l2FileDescriptor fd)
    {
        ulong counter;
        _ = ReadFd(fd, (byte*)&counter, sizeof(ulong));
    }

    /// <summary>Retries an ioctl through EINTR, returning the final result.</summary>
    internal static unsafe int IoctlRetry(V4l2FileDescriptor fd, nuint request, void* arg)
    {
        int rc;
        do { rc = Ioctl(fd, request, arg); }
        while (rc < 0 && Marshal.GetLastPInvokeError() == EINTR);
        return rc;
    }
}
