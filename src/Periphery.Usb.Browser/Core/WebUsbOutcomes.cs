// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Usb.Browser.Core;

/// <summary>Which WebUSB call a rejection came from. The same error name means different things in each.</summary>
internal enum WebUsbPhase
{
    /// <summary><c>open()</c> or <c>selectConfiguration()</c>.</summary>
    Open,

    /// <summary><c>claimInterface()</c>.</summary>
    Claim,

    /// <summary>A control, bulk or interrupt transfer, or <c>clearHalt()</c>.</summary>
    Transfer,
}

/// <summary>A WebUSB promise rejected with a <c>DOMException</c>.</summary>
/// <param name="Name">The exception's <c>name</c>, such as <c>NotFoundError</c>.</param>
/// <param name="Message">The exception's <c>message</c>, as the browser wrote it.</param>
internal sealed record WebUsbRejection(string Name, string Message);

/// <summary>What the shell does with a transfer that resolved with a <c>USBTransferStatus</c>.</summary>
/// <param name="Error">The exception to fail the transfer with, or <see langword="null"/> when it succeeded.</param>
/// <param name="ClearHaltFirst">Whether to call <c>clearHalt()</c> on the endpoint before failing.</param>
internal readonly record struct TransferStatusVerdict(Exception? Error, bool ClearHaltFirst)
{
    /// <summary>The transfer succeeded.</summary>
    public bool IsOk => Error is null;
}

/// <summary>
/// Maps every WebUSB outcome to Periphery's exceptions, so no <c>JSException</c> reaches a caller
/// (ADR-0092 D6). Total functions over the error name, the phase and the endpoint.
/// </summary>
/// <remarks>
/// <para>The names and their meanings are Chromium's (<c>usb_device.cc</c>).</para>
/// <para>A transfer reads <c>NotFoundError</c> as a removal. Chromium also uses it for an endpoint
/// outside the claimed interface, so the backend must check the endpoint against interface 0
/// before it issues a transfer. Disconnection and a revoked grant are then the only sources.</para>
/// </remarks>
internal static class WebUsbOutcomes
{
    internal const string NotFoundError = "NotFoundError";
    internal const string NetworkError = "NetworkError";
    internal const string SecurityError = "SecurityError";
    internal const string InvalidStateError = "InvalidStateError";
    internal const string AbortError = "AbortError";
    internal const string TimeoutError = "TimeoutError";

    internal const string StatusOk = "ok";
    internal const string StatusStall = "stall";
    internal const string StatusBabble = "babble";

    /// <summary>The exception a rejected WebUSB promise surfaces as.</summary>
    /// <param name="rejection">The <c>DOMException</c> the promise rejected with.</param>
    /// <param name="phase">The call that rejected.</param>
    /// <param name="endpoint">The endpoint address, for a transfer. Endpoint 0 is control.</param>
    /// <param name="deviceId">The device's <see cref="DeviceInfo.Id"/>.</param>
    public static Exception FromRejection(WebUsbRejection rejection, WebUsbPhase phase, byte endpoint, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        var inner = new IOException($"WebUSB {rejection.Name}: {rejection.Message}");

        // Only the backend's own close() aborts a pending call, so the device is being disposed.
        // The (message, inner) overload leaves ObjectName empty; keeping the browser's error is
        // worth more than the name.
        if (rejection.Name == AbortError)
            return new ObjectDisposedException($"USB device '{deviceId}' was closed.", inner);

        return phase switch
        {
            WebUsbPhase.Open => rejection.Name switch
            {
                NotFoundError => new UsbDeviceNotFoundException(
                    $"USB device '{deviceId}' is gone: it was unplugged, or its grant revoked, before it opened.",
                    inner, deviceId),
                SecurityError => new UsbAccessDeniedException(
                    $"The browser was denied access to USB device '{deviceId}'. On Linux the user needs a udev "
                    + "rule for it; on Windows its driver must be WinUSB.",
                    inner, deviceId),
                _ => new UsbException($"Opening USB device '{deviceId}' failed.", inner, deviceId),
            },

            WebUsbPhase.Claim => rejection.Name switch
            {
                NotFoundError => new UsbDeviceNotFoundException(
                    $"USB device '{deviceId}' left before interface 0 was claimed, or has no interface 0.",
                    inner, deviceId),
                SecurityError => new UsbAccessDeniedException(
                    $"Interface 0 of USB device '{deviceId}' is a class WebUSB does not allow: audio, HID, "
                    + "mass storage, smart card, video, audio/video or wireless controller.",
                    inner, deviceId),
                NetworkError => new UsbAccessDeniedException(
                    $"Interface 0 of USB device '{deviceId}' could not be claimed. Another process or driver "
                    + "may hold it.",
                    inner, deviceId),
                _ => new UsbException($"Claiming interface 0 of USB device '{deviceId}' failed.", inner, deviceId),
            },

            _ => rejection.Name switch
            {
                NotFoundError => new UsbDeviceRemovedException(
                    $"Transfer on endpoint 0x{endpoint:X2} failed: USB device '{deviceId}' was disconnected, "
                    + "or its grant was revoked.",
                    inner, deviceId),
                NetworkError or TimeoutError => new UsbTransferException(
                    $"Transfer on endpoint 0x{endpoint:X2} failed. The device is still attached.",
                    inner, deviceId),
                _ => new UsbException(
                    $"Transfer on endpoint 0x{endpoint:X2} of USB device '{deviceId}' was refused.",
                    inner, deviceId),
            },
        };
    }

    /// <summary>What a transfer that resolved with <paramref name="status"/> surfaces as.</summary>
    /// <remarks>
    /// <c>stall</c> and <c>babble</c> resolve the promise rather than reject it, so a shell that
    /// looked only at rejections would report them as success. A stall on a bulk or interrupt
    /// endpoint is cleared before the caller hears of it. A stall on endpoint 0 is a protocol stall,
    /// which the next setup packet clears, and <c>clearHalt()</c> does not apply there.
    /// </remarks>
    public static TransferStatusVerdict FromStatus(string status, byte endpoint, string deviceId)
    {
        if (status == StatusOk)
            return default;

        var inner = new IOException($"WebUSB transfer status '{status}'.");

        return status switch
        {
            StatusStall => new TransferStatusVerdict(
                new UsbTransferException(
                    $"Transfer on endpoint 0x{endpoint:X2} stalled: the device rejected the request. "
                    + "The device is still attached.",
                    inner, deviceId),
                ClearHaltFirst: (endpoint & 0x0F) != 0),
            StatusBabble => new TransferStatusVerdict(
                new UsbTransferException(
                    $"Transfer on endpoint 0x{endpoint:X2} babbled: the device sent more than was asked for.",
                    inner, deviceId),
                ClearHaltFirst: false),
            _ => new TransferStatusVerdict(
                new UsbTransferException(
                    $"Transfer on endpoint 0x{endpoint:X2} ended with an unknown status.", inner, deviceId),
                ClearHaltFirst: false),
        };
    }
}
