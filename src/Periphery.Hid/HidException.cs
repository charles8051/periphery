// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;

namespace Periphery.Hid;

/// <summary>
/// Base exception for all HID I/O failures. Catch this type to handle any
/// error from <see cref="HidDevice"/> or <see cref="HidDeviceProxy"/>.
/// The <see cref="Exception.InnerException"/> always contains the original
/// OS-level exception.
/// </summary>
public class HidException : IOException
{
    /// <summary>The device path or ID that was being accessed when the error occurred.</summary>
    public string? DeviceId { get; }

    public HidException(string message, string? deviceId = null)
        : base(message)
    {
        DeviceId = deviceId;
    }

    public HidException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}

/// <summary>
/// Thrown when the OS denies access to a HID device handle.
/// <para>
/// <b>Windows:</b> ERROR_ACCESS_DENIED (5) — the device class driver holds an
/// exclusive lock (common for keyboards and mice) or the process lacks privileges.
/// </para>
/// <para>
/// <b>Linux:</b> EACCES — the calling user does not have read/write permission on
/// <c>/dev/hidrawN</c>. Add a udev rule or run as root.
/// </para>
/// <para>
/// <b>macOS:</b> kIOReturnExclusiveAccess — another process has the device open
/// exclusively, or the app lacks the <c>com.apple.security.device.usb</c> entitlement.
/// </para>
/// </summary>
public sealed class HidAccessDeniedException : HidException
{
    public HidAccessDeniedException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// Thrown when the target HID device cannot be found — either it was never
/// present, was unplugged between enumeration and open, or the device node
/// no longer exists.
/// </summary>
public sealed class HidDeviceNotFoundException : HidException
{
    public HidDeviceNotFoundException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// Thrown when a read or write operation fails mid-transfer, typically because
/// the device was physically disconnected while I/O was in progress.
/// </summary>
public sealed class HidTransferException : HidException
{
    public HidTransferException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}
