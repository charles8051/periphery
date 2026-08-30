// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// The active 8080-style parallel interface. Obtain via
/// <see cref="TreehopperBoard.UseParallelAsync"/>; disposing it disables the module.
/// </summary>
/// <remarks>
/// Write-only: the firmware does not implement parallel reads, so there is no read
/// method (mirroring the original SDK). Words are 1 byte each for a data bus of ≤ 8
/// pins, 2 bytes (big-endian) for a wider bus.
/// </remarks>
public sealed class ParallelLease : IAsyncDisposable
{
    private readonly TreehopperBoard _board;
    private bool _disposed;

    internal ParallelLease(TreehopperBoard board, int busWidth)
    {
        _board = board;
        BusWidth = busWidth;
    }

    /// <summary>The data-bus width in bits (the number of data-bus pins).</summary>
    public int BusWidth { get; }

    /// <summary>Writes one or more words with the register-select line asserted for a command (RS = 0).</summary>
    public Task WriteCommandAsync(uint[] words, CancellationToken ct = default)
        => WriteAsync(isData: false, words, ct);

    /// <summary>Writes one or more words with the register-select line asserted for data (RS = 1).</summary>
    public Task WriteDataAsync(uint[] words, CancellationToken ct = default)
        => WriteAsync(isData: true, words, ct);

    private Task WriteAsync(bool isData, uint[] words, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Length == 0)
            return Task.CompletedTask;
        return _board.ExecuteTransactionAsync(
            new Command.ParallelWrite(isData, words.ToImmutableArray(), BusWidth), ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _board.ReconcileWithAsync(cfg => cfg with { Parallel = null }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
