// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;

namespace Periphery.Monitor;

/// <summary>
/// Base exception for monitor-control failures. Derives from
/// <see cref="IOException"/>, matching the family practice
/// (<c>HidException</c>, <c>UsbException</c>, <c>CameraException</c> —
/// ADR-0058 D11).
/// </summary>
public class MonitorException : IOException
{
    /// <summary>The enumeration identity of the monitor involved, when known.</summary>
    public string? DeviceId { get; }

    public MonitorException(string message, string? deviceId = null)
        : base(message)
    {
        DeviceId = deviceId;
    }

    public MonitorException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}

/// <summary>
/// The OS denied access to a monitor-control channel — insufficient
/// permissions on the i2c node (Linux) or a refused handle (Windows).
/// </summary>
public sealed class MonitorAccessDeniedException : MonitorException
{
    public MonitorAccessDeniedException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// The monitor was not found — unplugged between enumeration and open, or the
/// identity could not be resolved to any control channel.
/// </summary>
public sealed class MonitorDeviceNotFoundException : MonitorException
{
    public MonitorDeviceNotFoundException(string message, string? deviceId = null)
        : base(message, deviceId) { }

    public MonitorDeviceNotFoundException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>The monitor disappeared mid-operation (unplug, power loss).</summary>
public sealed class MonitorDeviceLostException : MonitorException
{
    public MonitorDeviceLostException(string message, string? deviceId = null)
        : base(message, deviceId) { }

    public MonitorDeviceLostException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// The requested operation belongs to a control plane this handle does not
/// have (ADR-0058 D7): VCP calls on a monitor with no DDC/CI channel, or
/// display-mode calls where no mode backend exists (e.g. Linux today).
/// Check <c>MonitorDevice.SupportsVcp</c> / <c>SupportsDisplayMode</c> first.
/// </summary>
public sealed class MonitorCapabilityException : MonitorException
{
    public MonitorCapabilityException(string message, string? deviceId = null)
        : base(message, deviceId) { }
}

/// <summary>
/// <c>SetDisplayConfig(SDC_VALIDATE)</c> rejected a requested topology
/// (ADR-0059 D3) - the loud pre-apply failure instead of a blanked panel.
/// </summary>
public sealed class MonitorLayoutRejectedException : MonitorException
{
    /// <summary>The CCD (SetDisplayConfig) return code.</summary>
    public int CcdReturnCode { get; }

    public MonitorLayoutRejectedException(string message, int ccdReturnCode)
        : base(message)
    {
        CcdReturnCode = ccdReturnCode;
    }
}

/// <summary>
/// A VCP transfer failed at the protocol level — the monitor rejected the
/// command, returned a malformed reply, or the channel timed out.
/// </summary>
public sealed class MonitorTransferException : MonitorException
{
    public MonitorTransferException(string message, string? deviceId = null)
        : base(message, deviceId) { }

    public MonitorTransferException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}
