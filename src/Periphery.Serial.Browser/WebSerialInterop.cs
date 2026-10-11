// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Periphery.Serial.Browser;

/// <summary>
/// The <c>[JSImport]</c> bindings to <c>wwwroot/periphery-serial.js</c>. Every async function
/// takes an operation id; a negative result, or <see langword="null"/>, means the call failed, and
/// <see cref="TakeError"/> returns why (ADR-0092 D10).
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebSerialInterop
{
    internal const string Module = "Periphery.Serial.Browser";
    internal const string ModulePath = "_content/Periphery.Serial.Browser/periphery-serial.js";

    private static int s_operations;

    internal static int NextOperation() => Interlocked.Increment(ref s_operations);

    [JSImport("isSupported", Module)]
    internal static partial bool IsSupported();

    [JSImport("requestPort", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string?> RequestPortAsync(int op, string filtersJson);

    [JSImport("open", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> OpenAsync(int op, int handle, string optionsJson);

    [JSImport("read", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> ReadAsync(int op, int handle, int max);

    [JSImport("write", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> WriteAsync(int op, int handle);

    [JSImport("setSignals", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> SetSignalsAsync(int op, int handle, string signalsJson);

    [JSImport("close", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> CloseAsync(int op, int handle);

    /// <summary>Copies the bytes a write will send. Call it just before that write, with its op id.</summary>
    [JSImport("stage", Module)]
    internal static partial void Stage(int op, [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

    /// <summary>Copies a completed read's bytes into <paramref name="destination"/>.</summary>
    [JSImport("takeData", Module)]
    internal static partial void TakeData(int op, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    /// <summary>The failed call's <c>DOMException</c> as JSON: <c>{"name": ..., "message": ...}</c>.</summary>
    [JSImport("takeError", Module)]
    internal static partial string TakeError(int op);
}
