// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// The hardware UART operating as a 1-Wire bus (Dallas/Maxim). Obtain via
/// <see cref="TreehopperBoard.UseOneWireAsync"/>; disposing it disables the UART.
/// </summary>
/// <remarks>
/// 1-Wire ties the TX (open-drain) and RX pins together to form a single
/// bidirectional data line; an external pull-up is usually required. This lease is
/// the substrate for 1-Wire peripherals such as the DS18B20 temperature sensor.
/// </remarks>
public sealed class OneWireLease : IAsyncDisposable
{
    private readonly TreehopperBoard _board;
    private bool _disposed;

    internal OneWireLease(TreehopperBoard board) => _board = board;

    /// <summary>
    /// Issues a 1-Wire reset pulse. Returns <see langword="true"/> if at least one
    /// device answered with a presence pulse.
    /// </summary>
    public async Task<bool> ResetAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = await _board.ExecuteTransactionAsync(new Command.OneWireReset(), ct).ConfigureAwait(false);
        return response.Length > 0 && response[0] > 0;
    }

    /// <summary>
    /// Searches the bus and returns the 64-bit ROM addresses of every device present.
    /// </summary>
    public Task<IReadOnlyList<ulong>> SearchAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _board.ExecuteOneWireSearchAsync(ct);
    }

    /// <summary>
    /// Resets the bus and selects a single device by ROM address (MATCH ROM, <c>0x55</c>),
    /// so subsequent <see cref="SendAsync"/> / <see cref="ReceiveAsync"/> calls target it.
    /// </summary>
    public async Task ResetAndMatchAsync(ulong romAddress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ResetAsync(ct).ConfigureAwait(false);

        var frame = new byte[9];
        frame[0] = 0x55; // MATCH ROM
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), romAddress);
        await SendAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>Writes bytes onto the 1-Wire bus (≤ 63 bytes per call).</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.Length > 63)
            throw new ArgumentOutOfRangeException(nameof(data), "1-Wire transmit is limited to 63 bytes per call.");
        return _board.ExecuteTransactionAsync(new Command.UartTransmit(data), ct);
    }

    /// <summary>Clocks in and returns <paramref name="count"/> bytes (≤ 32) from the bus.</summary>
    public async Task<byte[]> ReceiveAsync(int count, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);

        var response = await _board.ExecuteTransactionAsync(
            new Command.UartReceive(count), ct).ConfigureAwait(false);

        // Response: 33 bytes — data[0..31] + count at [32].
        if (response.Length < 33) return [];
        int n = Math.Min((int)response[32], 32);
        return response[..n];
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
