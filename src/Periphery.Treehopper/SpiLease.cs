// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper;

/// <summary>
/// The active SPI module on a Treehopper board. Obtain via
/// <see cref="TreehopperBoard.UseSpiAsync"/>; disposing it disables the module.
/// </summary>
/// <remarks>
/// Clock speed and polarity/phase are fixed at lease creation (the values passed
/// to <see cref="TreehopperBoard.UseSpiAsync"/>). They may be overridden
/// per-transfer.
/// </remarks>
public sealed class SpiLease : IAsyncDisposable
{
    private readonly TreehopperBoard _board;
    private readonly double _clockMhz;
    private readonly SpiMode _mode;
    private bool _disposed;

    internal SpiLease(TreehopperBoard board, double clockMhz, SpiMode mode)
    {
        _board = board;
        _clockMhz = clockMhz;
        _mode = mode;
    }

    /// <summary>
    /// Runs a SPI transfer. In <see cref="SpiBurstMode.NoBurst"/> (the default) this is
    /// full-duplex — <paramref name="tx"/> is sent while the same number of MISO bytes
    /// are clocked in and returned. See <see cref="WriteAsync"/> / <see cref="ReadAsync"/>
    /// for the one-directional burst shortcuts.
    /// </summary>
    /// <param name="tx">
    /// MOSI data. In <see cref="SpiBurstMode.BurstRx"/> the bytes are not sent — only the
    /// buffer's <em>length</em> matters (the number of MISO bytes to clock in).
    /// </param>
    /// <param name="chipSelectPin">Pin number to use as chip-select, or -1 for none.</param>
    /// <param name="chipSelectMode">How the chip-select pin is driven across the transfer.</param>
    /// <param name="burstMode">Full-duplex, transmit-only, or receive-only.</param>
    /// <param name="clockMhz">Override the lease's clock speed (MHz), or 0 to use the default.</param>
    /// <param name="mode">Override the lease's SPI mode, or <see langword="null"/> to use the default.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The MISO bytes (length equal to <paramref name="tx"/>), or an empty array in
    /// <see cref="SpiBurstMode.BurstTx"/>.
    /// </returns>
    public Task<byte[]> TransferAsync(
        ReadOnlyMemory<byte> tx,
        int chipSelectPin = -1,
        ChipSelectMode chipSelectMode = ChipSelectMode.SpiActiveLow,
        SpiBurstMode burstMode = SpiBurstMode.NoBurst,
        double clockMhz = 0,
        SpiMode? mode = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _board.ExecuteTransactionAsync(
            new Command.SpiTransaction(
                tx,
                chipSelectPin,
                ChipSelectMode: (byte)chipSelectMode,
                SpeedMhz: clockMhz > 0 ? clockMhz : _clockMhz,
                Mode: mode ?? _mode,
                Burst: (byte)burstMode,
                // The shell decides the danger-band policy once (env var read at board
                // type init) and stamps it onto the command; the codec stays pure.
                AllowDangerBand: TreehopperBoard.AllowSpiDangerBand),
            ct);
    }

    /// <summary>
    /// Transmit-only transfer (<see cref="SpiBurstMode.BurstTx"/>): sends
    /// <paramref name="tx"/> and returns nothing — the fastest mode, since it skips the
    /// MISO read round-trip. Ideal for write-only peripherals (LED strips, shift registers).
    /// </summary>
    public async Task WriteAsync(
        ReadOnlyMemory<byte> tx,
        int chipSelectPin = -1,
        ChipSelectMode chipSelectMode = ChipSelectMode.SpiActiveLow,
        double clockMhz = 0,
        SpiMode? mode = null,
        CancellationToken ct = default)
        => await TransferAsync(tx, chipSelectPin, chipSelectMode, SpiBurstMode.BurstTx, clockMhz, mode, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Receive-only transfer (<see cref="SpiBurstMode.BurstRx"/>): clocks in
    /// <paramref name="count"/> MISO bytes without sending MOSI data.
    /// </summary>
    public Task<byte[]> ReadAsync(
        int count,
        int chipSelectPin = -1,
        ChipSelectMode chipSelectMode = ChipSelectMode.SpiActiveLow,
        double clockMhz = 0,
        SpiMode? mode = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return TransferAsync(new byte[count], chipSelectPin, chipSelectMode, SpiBurstMode.BurstRx, clockMhz, mode, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _board.ReconcileWithAsync(cfg => cfg with { Spi = null }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* best-effort teardown */ }
    }
}
