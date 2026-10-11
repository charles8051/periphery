// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Periphery.Usb.Browser;

/// <summary>
/// The <c>[JSImport]</c> bindings to <c>wwwroot/periphery-usb.js</c>. Every async function takes
/// an operation id; a negative result means the call failed, and <see cref="TakeError"/> returns
/// why. A read's bytes are fetched with <see cref="TakeData"/> once its length is known (ADR-0092
/// D10).
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebUsbInterop
{
    /// <summary>The name <see cref="JSHost.ImportAsync"/> registers the module under.</summary>
    internal const string Module = "Periphery.Usb.Browser";

    /// <summary>The module's path under the document's base URI, where the Razor SDK serves it.</summary>
    internal const string ModulePath = "_content/Periphery.Usb.Browser/periphery-usb.js";

    internal const int Ok = 0;
    internal const int Rejected = -1;
    internal const int Stall = -2;
    internal const int Babble = -3;
    internal const int UnknownStatus = -4;

    private static int s_operations;

    /// <summary>A fresh operation id, so concurrent calls never share a result slot.</summary>
    internal static int NextOperation() => Interlocked.Increment(ref s_operations);

    [JSImport("isSupported", Module)]
    internal static partial bool IsSupported();

    [JSImport("getDevices", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string?> GetDevicesAsync(int op);

    [JSImport("requestDevice", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> RequestDeviceAsync(int op, string filtersJson);

    [JSImport("open", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> OpenAsync(int op, int handle);

    [JSImport("selectConfiguration", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> SelectConfigurationAsync(int op, int handle, int value);

    [JSImport("claimInterface", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> ClaimInterfaceAsync(int op, int handle, int interfaceNumber);

    [JSImport("close", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> CloseAsync(int op, int handle);

    [JSImport("activeConfiguration", Module)]
    internal static partial int ActiveConfiguration(int handle);

    [JSImport("configurationValues", Module)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    internal static partial int[] ConfigurationValues(int handle);

    [JSImport("controlTransferIn", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> ControlTransferInAsync(
        int op, int handle, string requestType, string recipient, int request, int value, int index, int length);

    [JSImport("controlTransferOut", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> ControlTransferOutAsync(
        int op, int handle, string requestType, string recipient, int request, int value, int index);

    [JSImport("transferIn", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> TransferInAsync(int op, int handle, int endpointNumber, int length);

    [JSImport("transferOut", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> TransferOutAsync(int op, int handle, int endpointNumber);

    [JSImport("clearHalt", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> ClearHaltAsync(int op, int handle, string direction, int endpointNumber);

    /// <summary>Copies the bytes an OUT call will send. Call it just before that call, with its op id.</summary>
    [JSImport("stage", Module)]
    internal static partial void Stage(int op, [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

    /// <summary>Copies a completed read's bytes into <paramref name="destination"/>.</summary>
    [JSImport("takeData", Module)]
    internal static partial void TakeData(int op, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    /// <summary>The failed call's <c>DOMException</c> as JSON: <c>{"name": ..., "message": ...}</c>.</summary>
    [JSImport("takeError", Module)]
    internal static partial string TakeError(int op);
}
