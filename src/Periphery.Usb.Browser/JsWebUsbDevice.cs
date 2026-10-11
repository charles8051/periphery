// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;
using Periphery.Usb.Browser.Core;

namespace Periphery.Usb.Browser;

/// <summary>
/// <see cref="IWebUsbDevice"/> over the JS module: one granted <c>USBDevice</c>, named by the
/// handle the module gave it.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class JsWebUsbDevice(int handle) : IWebUsbDevice
{
    public byte? ActiveConfigurationValue
    {
        get
        {
            int value = WebUsbInterop.ActiveConfiguration(handle);
            return value < 0 ? null : (byte)value;
        }
    }

    public IReadOnlyList<byte> ConfigurationValues
        => Array.ConvertAll(WebUsbInterop.ConfigurationValues(handle), v => (byte)v);

    public async Task OpenAsync()
    {
        int op = WebUsbInterop.NextOperation();
        Check(op, await WebUsbInterop.OpenAsync(op, handle).ConfigureAwait(false));
    }

    public async Task SelectConfigurationAsync(byte configurationValue)
    {
        int op = WebUsbInterop.NextOperation();
        Check(op, await WebUsbInterop.SelectConfigurationAsync(op, handle, configurationValue).ConfigureAwait(false));
    }

    public async Task ClaimInterfaceAsync(byte interfaceNumber)
    {
        int op = WebUsbInterop.NextOperation();
        Check(op, await WebUsbInterop.ClaimInterfaceAsync(op, handle, interfaceNumber).ConfigureAwait(false));
    }

    public async Task CloseAsync()
    {
        int op = WebUsbInterop.NextOperation();
        Check(op, await WebUsbInterop.CloseAsync(op, handle).ConfigureAwait(false));
    }

    public async Task<WebUsbInResult> ControlTransferInAsync(UsbControlSetup setup, int length)
    {
        var (requestType, recipient) = SetupNames(setup);
        int op = WebUsbInterop.NextOperation();
        int code = await WebUsbInterop.ControlTransferInAsync(
            op, handle, requestType, recipient, setup.Request, setup.Value, setup.Index, length).ConfigureAwait(false);
        return InResult(op, code);
    }

    public async Task<WebUsbOutResult> ControlTransferOutAsync(UsbControlSetup setup, ReadOnlyMemory<byte> data)
    {
        var (requestType, recipient) = SetupNames(setup);
        int op = WebUsbInterop.NextOperation();
        Stage(op, data);
        int code = await WebUsbInterop.ControlTransferOutAsync(
            op, handle, requestType, recipient, setup.Request, setup.Value, setup.Index).ConfigureAwait(false);
        return OutResult(op, code);
    }

    public async Task<WebUsbInResult> TransferInAsync(byte endpointNumber, int length)
    {
        int op = WebUsbInterop.NextOperation();
        int code = await WebUsbInterop.TransferInAsync(op, handle, endpointNumber, length).ConfigureAwait(false);
        return InResult(op, code);
    }

    public async Task<WebUsbOutResult> TransferOutAsync(byte endpointNumber, ReadOnlyMemory<byte> data)
    {
        int op = WebUsbInterop.NextOperation();
        Stage(op, data);
        int code = await WebUsbInterop.TransferOutAsync(op, handle, endpointNumber).ConfigureAwait(false);
        return OutResult(op, code);
    }

    public async Task ClearHaltAsync(UsbTransferDirection direction, byte endpointNumber)
    {
        int op = WebUsbInterop.NextOperation();
        string name = direction == UsbTransferDirection.DeviceToHost ? "in" : "out";
        Check(op, await WebUsbInterop.ClearHaltAsync(op, handle, name, endpointNumber).ConfigureAwait(false));
    }

    // The module copies the bytes before the call returns, so the caller's buffer is free again
    // while the transfer is still pending (the IWebUsbDevice contract).
    private static void Stage(int op, ReadOnlyMemory<byte> data)
    {
        var copy = data.ToArray();
        WebUsbInterop.Stage(op, copy);
    }

    private static (string RequestType, string Recipient) SetupNames(UsbControlSetup setup)
        => WebUsbSetup.Names(setup.RequestType)
           ?? throw new WebUsbRejectionException(new WebUsbRejection(
               "TypeError",
               $"bmRequestType 0x{setup.RequestType:X2} uses a reserved type or recipient, which WebUSB cannot send."));

    private static WebUsbInResult InResult(int op, int code)
    {
        switch (code)
        {
            case >= 0:
                var data = new byte[code];
                if (code > 0)
                    WebUsbInterop.TakeData(op, data);
                return new WebUsbInResult(WebUsbOutcomes.StatusOk, data);
            case WebUsbInterop.Stall:
                return new WebUsbInResult(WebUsbOutcomes.StatusStall, ReadOnlyMemory<byte>.Empty);
            case WebUsbInterop.Babble:
                return new WebUsbInResult(WebUsbOutcomes.StatusBabble, ReadOnlyMemory<byte>.Empty);
            case WebUsbInterop.UnknownStatus:
                return new WebUsbInResult("unknown", ReadOnlyMemory<byte>.Empty);
            default:
                throw Rejected(op);
        }
    }

    private static WebUsbOutResult OutResult(int op, int code) => code switch
    {
        >= 0 => new WebUsbOutResult(WebUsbOutcomes.StatusOk, code),
        WebUsbInterop.Stall => new WebUsbOutResult(WebUsbOutcomes.StatusStall, 0),
        WebUsbInterop.Babble => new WebUsbOutResult(WebUsbOutcomes.StatusBabble, 0),
        WebUsbInterop.UnknownStatus => new WebUsbOutResult("unknown", 0),
        _ => throw Rejected(op),
    };

    private static void Check(int op, int code)
    {
        if (code < 0)
            throw Rejected(op);
    }

    private static WebUsbRejectionException Rejected(int op)
        => new(WebUsbJson.ReadRejection(WebUsbInterop.TakeError(op)));
}
