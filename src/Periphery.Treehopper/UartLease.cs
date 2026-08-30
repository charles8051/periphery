// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// The active UART on a Treehopper board. Obtain via
/// <see cref="TreehopperBoard.UseUartAsync"/>; disposing it disables the UART.
/// </summary>
/// <remarks>
/// The firmware's UART transmit payload is limited to 63 bytes per call (one
/// 64-byte USB bulk packet minus the length byte).
/// </remarks>
public sealed class UartLease : IAsyncDisposable
{
    private readonly TreehopperBoard _board;
    private bool _disposed;

    internal UartLease(TreehopperBoard board) => _board = board;

    /// <summary>
    /// Transmits <paramref name="data"/> over the UART (≤ 63 bytes per call).
    /// </summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.Length > 63)
            throw new ArgumentOutOfRangeException(nameof(data),
                "UART transmit is limited to 63 bytes per call.");
        return _board.ExecuteTransactionAsync(new Command.UartTransmit(data), ct);
    }

    /// <summary>
    /// Reads bytes from the UART receive buffer. Returns up to 32 bytes; the
    /// actual count is read from the trailing byte of the firmware's 33-byte
    /// response.
    /// </summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = await _board.ExecuteTransactionAsync(
            new Command.UartReceive(), ct).ConfigureAwait(false);

        // Response: 33 bytes — data[0..31] + count at [32]
        if (response.Length < 33) return [];
        int count = Math.Min((int)response[32], 32);
        return response[..count];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _board.ReconcileWithAsync(cfg => cfg with { Uart = null }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
