// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Stm32.Serial;

/// <summary>
/// Wire and timing settings for the AN3155 UART bootloader. Immutable; reuse
/// <see cref="Default"/> or <c>with</c>-edit it.
/// </summary>
/// <remarks>
/// The frame format is not configurable: AN3155 §2 fixes it at 8 data bits, <b>even</b> parity,
/// 1 stop bit, and the programmer opens the port that way. Only the rate is a choice, and the
/// system bootloader autobauds to it from the 0x7F sync byte.
/// <para>
/// Every property validates in its <c>init</c> accessor. The protocol limits are real limits — a
/// transfer above <see cref="MaxTransferSize"/> makes the underlying AN3155 client throw from
/// inside a flash — so they are rejected where the caller can see them rather than mid-write.
/// </para>
/// </remarks>
public sealed record Stm32SerialOptions
{
    /// <summary>
    /// Bytes per Write Memory / Read Memory command that AN3155 permits: 256.
    /// </summary>
    public const int MaxTransferSize = 256;

    private readonly int _baudRate = 115200;
    private readonly int _erasePageSize = 2048;
    private readonly int _writeChunkSize = MaxTransferSize;
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _syncTimeout = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _eraseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Port rate. The bootloader autobauds, so this only has to be one it supports (1200–115200).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public int BaudRate
    {
        get => _baudRate;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _baudRate = value;
        }
    }

    /// <summary>
    /// Flash page size used to convert an image's extent into an Extended Erase page count.
    /// <para>
    /// There is no chip database yet (ADR-0061 phase 2), so the page size cannot be resolved from
    /// the device and this value is trusted as given. 2048 is the common STM32 mid-range page;
    /// F1 medium-density is 1024, and F2/F4 use non-uniform sectors that this flat model does not
    /// describe. Set it for the target, or use <see cref="EraseMode.None"/> and erase separately.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public int ErasePageSize
    {
        get => _erasePageSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _erasePageSize = value;
        }
    }

    /// <summary>
    /// Bytes per Write Memory / Read Memory command. AN3155 caps both at
    /// <see cref="MaxTransferSize"/>, and this rejects anything larger.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive, or above <see cref="MaxTransferSize"/>.</exception>
    public int WriteChunkSize
    {
        get => _writeChunkSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxTransferSize);
            _writeChunkSize = value;
        }
    }

    /// <summary>How long a single command may wait for its reply before the flash fails.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public TimeSpan CommandTimeout
    {
        get => _commandTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _commandTimeout = value;
        }
    }

    /// <summary>
    /// How long the AN3155 sync handshake waits for an answer. Deliberately much shorter than
    /// <see cref="CommandTimeout"/>: on a part that has already synced since reset, the sync byte
    /// is taken as a command opcode and silence is the <i>expected</i> first outcome, so this
    /// deadline is paid on every open of an already-synced part rather than only on a failure.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public TimeSpan SyncTimeout
    {
        get => _syncTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _syncTimeout = value;
        }
    }

    /// <summary>How long Extended Erase may take. Erasing a whole part takes tens of seconds on some families.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public TimeSpan EraseTimeout
    {
        get => _eraseTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _eraseTimeout = value;
        }
    }

    /// <summary>115200 8E1, 2 KiB pages, 256-byte transfers, 5 s per command, 30 s for erase.</summary>
    public static Stm32SerialOptions Default { get; } = new();
}
