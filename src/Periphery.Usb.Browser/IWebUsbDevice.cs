// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Usb.Browser.Core;

namespace Periphery.Usb.Browser;

/// <summary>
/// The calls <see cref="WebUsbBackend"/> makes on one granted WebUSB <c>USBDevice</c>, one per
/// method of that object. In a browser each forwards to the package's JS module; tests supply a
/// fake.
/// </summary>
/// <remarks>
/// <para>A method whose JS counterpart returns a promise returns a task. A rejected promise faults
/// the task with <see cref="WebUsbRejectionException"/>, carrying the <c>DOMException</c>'s name
/// and message. Nothing here can cancel a call; WebUSB has no way to.</para>
/// <para>A method that takes data copies it before it returns, so the caller may reuse its buffer
/// once the call returns, while the transfer is still pending.</para>
/// </remarks>
internal interface IWebUsbDevice
{
    /// <summary>
    /// <c>configuration?.configurationValue</c>: the active configuration, or
    /// <see langword="null"/> when the device is unconfigured.
    /// </summary>
    byte? ActiveConfigurationValue { get; }

    /// <summary>The <c>configurationValue</c> of each entry of <c>configurations</c>, in order.</summary>
    IReadOnlyList<byte> ConfigurationValues { get; }

    /// <summary><c>open()</c>.</summary>
    Task OpenAsync();

    /// <summary><c>selectConfiguration(configurationValue)</c>.</summary>
    Task SelectConfigurationAsync(byte configurationValue);

    /// <summary><c>claimInterface(interfaceNumber)</c>.</summary>
    Task ClaimInterfaceAsync(byte interfaceNumber);

    /// <summary><c>close()</c>. Pending calls reject with <c>AbortError</c>.</summary>
    Task CloseAsync();

    /// <summary><c>controlTransferIn(setup, length)</c>.</summary>
    Task<WebUsbInResult> ControlTransferInAsync(UsbControlSetup setup, int length);

    /// <summary><c>controlTransferOut(setup, data)</c>.</summary>
    Task<WebUsbOutResult> ControlTransferOutAsync(UsbControlSetup setup, ReadOnlyMemory<byte> data);

    /// <summary><c>transferIn(endpointNumber, length)</c>.</summary>
    Task<WebUsbInResult> TransferInAsync(byte endpointNumber, int length);

    /// <summary><c>transferOut(endpointNumber, data)</c>.</summary>
    Task<WebUsbOutResult> TransferOutAsync(byte endpointNumber, ReadOnlyMemory<byte> data);

    /// <summary><c>clearHalt(direction, endpointNumber)</c>.</summary>
    Task ClearHaltAsync(UsbTransferDirection direction, byte endpointNumber);
}

/// <summary>A resolved <c>USBInTransferResult</c>: its <c>status</c> and its <c>data</c>.</summary>
internal readonly record struct WebUsbInResult(string Status, ReadOnlyMemory<byte> Data);

/// <summary>A resolved <c>USBOutTransferResult</c>: its <c>status</c> and <c>bytesWritten</c>.</summary>
internal readonly record struct WebUsbOutResult(string Status, int BytesWritten);

/// <summary>A WebUSB promise rejected. <see cref="WebUsbBackend"/> maps it with <see cref="WebUsbOutcomes"/>.</summary>
internal sealed class WebUsbRejectionException : Exception
{
    public WebUsbRejectionException(WebUsbRejection rejection)
        : base($"{rejection.Name}: {rejection.Message}")
    {
        Rejection = rejection;
    }

    /// <summary>The <c>DOMException</c>'s name and message.</summary>
    public WebUsbRejection Rejection { get; }
}
