// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Periphery.Monitor.Linux;

/// <summary>
/// P/Invoke declarations for the Linux DDC/CI backend: <c>libc.so.6</c> file
/// I/O against <c>/dev/i2c-N</c> plus the <c>i2c-dev</c> slave-address ioctl.
/// All declarations use <see cref="LibraryImportAttribute"/> for AOT/trim
/// safety, mirroring the other Linux backends (ADR-0057).
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class I2cInterop
{
    private const string LibC = "libc.so.6";

    internal const int O_RDWR = 0x2;
    internal const int O_CLOEXEC = 0x80000;

    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int EIO = 5;
    internal const int ENXIO = 6;
    internal const int EACCES = 13;
    internal const int EBUSY = 16;
    internal const int ENODEV = 19;
    internal const int ETIMEDOUT = 110;
    internal const int EREMOTEIO = 121;

    /// <summary>i2c-dev: bind the fd to a 7-bit slave address.</summary>
    internal const nuint I2C_SLAVE = 0x0703;

    /// <summary>The DDC/CI slave address.</summary>
    internal const int DdcSlaveAddress = 0x37;

    [LibraryImport(LibC, EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags);

    [LibraryImport(LibC, EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport(LibC, EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport(LibC, EntryPoint = "write", SetLastError = true)]
    internal static unsafe partial nint Write(int fd, byte* buffer, nuint count);

    [LibraryImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, nint arg);
}
