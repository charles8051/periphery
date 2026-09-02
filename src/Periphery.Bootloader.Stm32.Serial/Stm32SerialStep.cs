// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Stm32.Serial;

/// <summary>
/// One step of a flash plan produced by <see cref="Stm32SerialPlan"/> and executed by the
/// programmer shell. A closed union; pure description, no IO.
/// </summary>
internal abstract record Stm32SerialStep
{
    private protected Stm32SerialStep() { }

    /// <summary>
    /// Extended Erase of <paramref name="PageCount"/> pages starting at page 0.
    /// <para>
    /// Starting at page 0 is not a choice — AN3155's Extended Erase takes an explicit page list,
    /// and the CallAndResponse client builds that list as 0..N. Erasing a window that starts
    /// above page 0 needs an upstream change.
    /// </para>
    /// </summary>
    public sealed record ErasePages(int PageCount) : Stm32SerialStep;

    /// <summary>Write Memory of one chunk at an absolute address (AN3155 caps a chunk at 256 bytes).</summary>
    public sealed record Write(uint Address, ReadOnlyMemory<byte> Data) : Stm32SerialStep;

    /// <summary>
    /// Read this segment back via Read Memory and compare to <see cref="Expected"/>. Emitted after
    /// all writes and before <see cref="Go"/> — a jumped device no longer answers the bootloader.
    /// </summary>
    public sealed record Verify(uint Address, ReadOnlyMemory<byte> Expected) : Stm32SerialStep;

    /// <summary>Leave the bootloader and jump to the application at <paramref name="JumpAddress"/>.</summary>
    public sealed record Go(uint JumpAddress) : Stm32SerialStep;
}
