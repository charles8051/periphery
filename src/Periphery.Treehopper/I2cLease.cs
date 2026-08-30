// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// The active I²C module on a Treehopper board. Obtain via
/// <see cref="TreehopperBoard.UseI2cAsync"/>; disposing it disables the module.
/// </summary>
public sealed class I2cLease : IAsyncDisposable
{
    private readonly TreehopperBoard _board;
    private bool _disposed;

    internal I2cLease(TreehopperBoard board) => _board = board;

    /// <summary>
    /// Sends <paramref name="tx"/> to <paramref name="address"/> and reads back
    /// <paramref name="readLength"/> bytes. Either side may be zero (write-only or
    /// read-only).
    /// </summary>
    /// <exception cref="TreehopperI2cException">The device NAKed or a bus error occurred.</exception>
    public async Task<byte[]> SendReceiveAsync(
        byte address,
        ReadOnlyMemory<byte> tx,
        int readLength,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var response = await _board.ExecuteTransactionAsync(
            new Command.I2cTransaction(address, tx, readLength), ct).ConfigureAwait(false);

        // Response: [status, data…]. 0xFF = success.
        byte status = response.Length > 0 ? response[0] : (byte)0;
        if (status != TreehopperWire.I2cSuccess)
            throw new TreehopperI2cException(address, (I2cTransferError)status);

        return readLength > 0 ? response[1..] : [];
    }

    /// <summary>Writes <paramref name="data"/> to <paramref name="address"/> (no read stage).</summary>
    /// <exception cref="TreehopperI2cException">The device NAKed or a bus error occurred.</exception>
    public async Task WriteAsync(byte address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => await SendReceiveAsync(address, data, readLength: 0, ct).ConfigureAwait(false);

    /// <summary>Reads <paramref name="count"/> bytes from <paramref name="address"/> (no write stage).</summary>
    /// <exception cref="TreehopperI2cException">The device NAKed or a bus error occurred.</exception>
    public Task<byte[]> ReadAsync(byte address, int count, CancellationToken ct = default)
        => SendReceiveAsync(address, ReadOnlyMemory<byte>.Empty, count, ct);

    /// <summary>
    /// Writes <paramref name="write"/> then reads <paramref name="readCount"/> bytes
    /// from <paramref name="address"/> in one transaction (e.g. write a register
    /// pointer, then read its value). An alias for <see cref="SendReceiveAsync"/>.
    /// </summary>
    /// <exception cref="TreehopperI2cException">The device NAKed or a bus error occurred.</exception>
    public Task<byte[]> WriteReadAsync(
        byte address, ReadOnlyMemory<byte> write, int readCount, CancellationToken ct = default)
        => SendReceiveAsync(address, write, readCount, ct);

    /// <summary>
    /// Probes whether a device is present at <paramref name="address"/> (sends a
    /// zero-byte write and returns <c>true</c> on ACK, <c>false</c> on NACK).
    /// </summary>
    public async Task<bool> PingAsync(byte address, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var response = await _board.ExecuteTransactionAsync(
            new Command.I2cTransaction(address, ReadOnlyMemory<byte>.Empty, 0), ct).ConfigureAwait(false);

        byte status = response.Length > 0 ? response[0] : (byte)0;
        return status == TreehopperWire.I2cSuccess;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _board.ReconcileWithAsync(cfg => cfg with { I2c = null }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
