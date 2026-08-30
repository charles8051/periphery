// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO;

namespace Periphery.Camera;

/// <summary>
/// Base exception for all camera I/O failures. Catch this type to handle any
/// error from <see cref="CameraDevice"/> or <see cref="CameraSession"/>.
/// </summary>
public class CameraException : IOException
{
    /// <summary>The device path or ID that was being accessed when the error occurred.</summary>
    public string? DeviceId { get; }

    public CameraException(string message, string? deviceId = null)
        : base(message) => DeviceId = deviceId;

    public CameraException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException) => DeviceId = deviceId;
}

/// <summary>
/// Thrown when the OS denies access to a camera device — privacy policy,
/// TCC prompt declined, or insufficient privileges.
/// </summary>
public sealed class CameraAccessDeniedException : CameraException
{
    public CameraAccessDeniedException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// Thrown when the target camera device cannot be found — unplugged between
/// enumeration and open, or the device node no longer exists.
/// </summary>
public sealed class CameraDeviceNotFoundException : CameraException
{
    public CameraDeviceNotFoundException(string message, string? deviceId = null)
        : base(message, deviceId) { }

    public CameraDeviceNotFoundException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// Thrown when the device is physically disconnected or lost during an active
/// capture or control operation. Carries the <see cref="DeviceInfo"/> of the
/// lost device to support reconnect orchestration.
/// </summary>
public sealed class CameraDeviceLostException : CameraException
{
    public CameraDeviceLostException(string message, string? deviceId = null)
        : base(message, deviceId) { }

    public CameraDeviceLostException(string message, Exception innerException, string? deviceId = null)
        : base(message, innerException, deviceId) { }
}

/// <summary>
/// Thrown when a requested format or configuration cannot be negotiated
/// with the camera backend.
/// </summary>
public sealed class CameraConfigurationException : CameraException
{
    public CameraConfigurationException(string message, string? deviceId = null)
        : base(message, deviceId) { }
}

/// <summary>
/// Thrown when the camera stops delivering frames within
/// <see cref="CameraCaptureOptions.FrameTimeout"/>. The device is still
/// connected (a true disconnect surfaces as
/// <see cref="CameraDeviceLostException"/>); it has just stalled the
/// stream — common with USB cameras under bandwidth pressure or when a
/// driver-level hiccup blocks ReadSample / equivalent indefinitely.
/// </summary>
public sealed class CameraTimeoutException : CameraException
{
    public CameraTimeoutException(string message, string? deviceId = null)
        : base(message, deviceId) { }
}
