// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;
using Periphery.Serial.Browser.Core;

namespace Periphery.Serial.Browser;

/// <summary><see cref="IWebSerialPort"/> over the JS module: one granted <c>SerialPort</c>, named by its handle.</summary>
[SupportedOSPlatform("browser")]
internal sealed class JsWebSerialPort(int handle) : IWebSerialPort
{
    public async Task OpenAsync(string optionsJson)
    {
        int op = WebSerialInterop.NextOperation();
        Check(op, await WebSerialInterop.OpenAsync(op, handle, optionsJson).ConfigureAwait(false));
    }

    public async Task<int> ReadAsync(Memory<byte> destination)
    {
        int op = WebSerialInterop.NextOperation();
        int count = await WebSerialInterop.ReadAsync(op, handle, destination.Length).ConfigureAwait(false);
        Check(op, count);
        if (count > 0)
            WebSerialInterop.TakeData(op, destination.Span[..count]);
        return count;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        int op = WebSerialInterop.NextOperation();
        // The module copies the bytes before the write is awaited.
        WebSerialInterop.Stage(op, data.ToArray());
        Check(op, await WebSerialInterop.WriteAsync(op, handle).ConfigureAwait(false));
    }

    public async Task SetSignalsAsync(string signalsJson)
    {
        int op = WebSerialInterop.NextOperation();
        Check(op, await WebSerialInterop.SetSignalsAsync(op, handle, signalsJson).ConfigureAwait(false));
    }

    public async Task CloseAsync()
    {
        int op = WebSerialInterop.NextOperation();
        Check(op, await WebSerialInterop.CloseAsync(op, handle).ConfigureAwait(false));
    }

    private static void Check(int op, int code)
    {
        if (code < 0)
            throw new WebSerialRejectionException(WebSerialJson.ReadRejection(WebSerialInterop.TakeError(op)));
    }
}
