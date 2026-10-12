// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Usb;

namespace Periphery.DebugProbe.StLink;

/// <summary>
/// One ST-Link exchange: a 16-byte command, optional data out, and a reply of a known length.
/// The seam between the ST-Link session and USB, so the session runs against a fake in tests.
/// </summary>
internal interface IStLinkTransport
{
    Task<byte[]> ExchangeAsync(byte[] command, ReadOnlyMemory<byte> dataOut, int replyLength, CancellationToken ct);
}

/// <summary>The ST-Link transport over a <see cref="UsbDevice"/> opened on interface 0.</summary>
internal sealed class UsbStLinkTransport : IStLinkTransport
{
    private readonly UsbDevice _usb;
    private readonly StLinkModel _model;

    public UsbStLinkTransport(UsbDevice usb, StLinkModel model)
    {
        _usb = usb;
        _model = model;
    }

    public async Task<byte[]> ExchangeAsync(byte[] command, ReadOnlyMemory<byte> dataOut, int replyLength, CancellationToken ct)
    {
        await WriteAllAsync(command, ct).ConfigureAwait(false);
        if (!dataOut.IsEmpty)
            await WriteAllAsync(dataOut, ct).ConfigureAwait(false);

        var reply = new byte[replyLength];
        int read = 0;
        while (read < replyLength)
        {
            var chunk = await _usb.BulkReadAsync(_model.InEndpoint, replyLength - read, ct).ConfigureAwait(false);
            if (chunk.Length == 0)
                throw new StLinkException($"ST-Link reply ended after {read} of {replyLength} bytes.");
            chunk.CopyTo(reply, read);
            read += chunk.Length;
        }
        return reply;
    }

    private async Task WriteAllAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            int written = await _usb.BulkWriteAsync(_model.OutEndpoint, data, ct).ConfigureAwait(false);
            if (written <= 0)
                throw new StLinkException("ST-Link accepted no bytes on its OUT endpoint.");
            data = data[written..];
        }
    }
}
