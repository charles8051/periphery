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

/// <summary>
/// Thrown when a camera is opened while a previous session's teardown of the
/// same device is still running in the background after overrunning its budget
/// (issue #123).
/// </summary>
/// <remarks>
/// <para>
/// The device is still enumerated and may still report as active. What holds it
/// is a thread inside a driver call that never returned, typically a wedged
/// Media Foundation Shutdown or Flush. Opening into that contends for the
/// device, and on a wedged driver the open fails in a way that looks like a
/// stream fault, which is how one wedge became nineteen consecutive failures.
/// Refusing the open and naming the cause is the honest answer; the thread
/// cannot be cancelled.
/// </para>
/// <para>
/// <see cref="Completion"/> completes when the abandoned work does, so a caller
/// that would rather wait than fail can
/// <c>await ex.Completion.WaitAsync(timeout, ct)</c> and retry. It may never
/// complete if the driver is truly wedged; replugging the camera is then the
/// only recovery.
/// </para>
/// </remarks>
public sealed class CameraTeardownPendingException : CameraException
{
    public CameraTeardownPendingException(
        string message, string? deviceId, Task completion, TimeSpan pendingFor)
        : base(message, deviceId)
    {
        ArgumentNullException.ThrowIfNull(completion);
        Completion = completion;
        PendingFor = pendingFor;
    }

    /// <summary>
    /// Completes, always successfully, when every abandoned teardown step on the
    /// device has finished. Never faults and is never cancelled.
    /// </summary>
    /// <remarks>
    /// <b>Bounded, not open-ended.</b> A teardown step wedged in a driver call
    /// may never return, so this also completes once the refusal window from the
    /// first overrun elapses. Awaiting it is therefore a wait of at most that
    /// window, and completion means "the device may be opened again", not "the
    /// abandoned work finished". The retry that follows can still fail, and is an
    /// ordinary open failure for the caller's recovery policy to handle.
    /// </remarks>
    public Task Completion { get; }

    /// <summary>
    /// How long the teardown had been pending when this was thrown, measured
    /// from the first step to overrun its budget.
    /// </summary>
    public TimeSpan PendingFor { get; }
}
