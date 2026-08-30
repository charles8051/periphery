// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;

namespace Periphery.Usb;

/// <summary>
/// Base exception for all USB I/O failures. The
/// <see cref="Exception.InnerException"/> carries the original OS-level error.
/// </summary>
public class UsbException : IOException
{
    /// <summary>The device path or ID that was being accessed when the error occurred.</summary>
    public string? DeviceId { get; }

    public UsbException(string message, string? deviceId = null)
        : base(message)
    {
        DeviceId = deviceId;
    }

    public UsbException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}

/// <summary>
/// The device exists but the handle could not be opened — another driver owns
/// it (e.g. it is bound to a class driver, not WinUSB) or the process lacks
/// sufficient privilege.
/// </summary>
public sealed class UsbAccessDeniedException : UsbException
{
    public UsbAccessDeniedException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>The device could not be found when the handle was opened.</summary>
/// <remarks>
/// The open-time counterpart of <see cref="UsbDeviceRemovedException"/>: this one means the
/// device was not there when we looked, which usually means a stale identity or a device that
/// is simply not plugged in.
/// </remarks>
public sealed class UsbDeviceNotFoundException : UsbException
{
    public UsbDeviceNotFoundException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// The device left the USB bus while a transfer was in flight — a surprise removal, not a
/// transport fault.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="UsbTransferException"/>, which means the transfer
/// failed while the device stayed on the bus. The two want different responses: a removal is a
/// wait-for-re-arrival, a transfer fault is a retry or a reset. They were indistinguishable
/// until #260, where a removal presented as a generic transfer failure and cost an
/// investigation an hour on a firmware hypothesis the fault code had already excluded.
/// <para>
/// It <em>derives from</em> <see cref="UsbTransferException"/> rather than sitting beside it: a
/// removal really is one way a transfer fails, so <c>catch (UsbTransferException)</c> keeps
/// catching unplugs and a caller that wants to treat them differently catches this first. The
/// specialisation adds information without taking any away (#272 review turn 1).
/// </para>
/// <para>
/// Distinct from <see cref="UsbDeviceNotFoundException"/>, which is the open-time case and is
/// not a transfer failure at all: there the device was never there, here it was ours and
/// vanished.
/// </para>
/// <para>
/// Not every removal can be reported as one. Windows returns
/// <c>ERROR_GEN_FAILURE</c> / <c>ERROR_BAD_COMMAND</c> for a removal and for a stalled pipe
/// alike, so a transfer that meets one of those surfaces as
/// <see cref="UsbTransferException"/> with the ambiguity spelled out rather than guessed at.
/// libusb has no such problem — it reports <c>LIBUSB_TRANSFER_NO_DEVICE</c> outright.
/// </para>
/// </remarks>
public sealed class UsbDeviceRemovedException : UsbTransferException
{
    public UsbDeviceRemovedException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>A control, bulk, or interrupt transfer failed mid-flight.</summary>
/// <remarks>
/// Unsealed so <see cref="UsbDeviceRemovedException"/> can specialise it: a removal is one way
/// a transfer fails, so a caller handling transfer failures generally should keep catching it,
/// and one that wants to treat an unplug differently catches the derived type first.
/// </remarks>
public class UsbTransferException : UsbException
{
    public UsbTransferException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// A transfer did not complete within its configured deadline — the endpoint is most
/// likely wedged (a firmware hang that stopped draining the endpoint, or an unplugged
/// device whose completion never arrives). Distinct from
/// <see cref="OperationCanceledException"/>, which signals caller-requested cancellation:
/// a timeout is a fault the caller did not ask for and almost always wants to surface
/// (reconnect, alert) rather than swallow.
/// </summary>
public sealed class UsbTimeoutException : UsbException
{
    /// <summary>The deadline that elapsed before the transfer completed.</summary>
    public TimeSpan Timeout { get; }

    public UsbTimeoutException(string message, string? deviceId, TimeSpan timeout)
        : base(message, deviceId)
    {
        Timeout = timeout;
    }
}
