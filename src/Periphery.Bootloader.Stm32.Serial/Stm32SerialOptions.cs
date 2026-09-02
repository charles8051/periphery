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
/// </remarks>
public sealed record Stm32SerialOptions
{
    /// <summary>Port rate. The bootloader autobauds, so this only has to be one it supports (1200–115200).</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>
    /// Flash page size used to convert an image's extent into an Extended Erase page count.
    /// <para>
    /// There is no chip database yet (ADR-0061 phase 2), so the page size cannot be resolved from
    /// the device and this value is trusted as given. 2048 is the common STM32 mid-range page;
    /// F1 medium-density is 1024, and F2/F4 use non-uniform sectors that this flat model does not
    /// describe. Set it for the target, or use <see cref="EraseMode.None"/> and erase separately.
    /// </para>
    /// </summary>
    public int ErasePageSize { get; init; } = 2048;

    /// <summary>Bytes per Write Memory / Read Memory command. AN3155 caps both at 256.</summary>
    public int WriteChunkSize { get; init; } = 256;

    /// <summary>How long a single command may wait for its reply before the flash fails.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long Extended Erase may take. Erasing a whole part takes tens of seconds on some families.</summary>
    public TimeSpan EraseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>115200 8E1, 2 KiB pages, 256-byte transfers, 5 s per command, 30 s for erase.</summary>
    public static Stm32SerialOptions Default { get; } = new();
}
