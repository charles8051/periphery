// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// One step of a flash plan produced by <see cref="Stm32DfuPlan"/> and executed by the
/// programmer shell. A closed union; pure description, no IO.
/// </summary>
internal abstract record DfuStep
{
    private protected DfuStep() { }

    /// <summary>Mass-erase the flash before writing.</summary>
    public sealed record MassErase : DfuStep
    {
        public static readonly MassErase Instance = new();
    }

    /// <summary>Set the address pointer to the base of the region about to be written.</summary>
    public sealed record SetAddress(uint Address) : DfuStep;

    /// <summary>
    /// Write one block via DFU_DNLOAD with this <c>wBlockNum</c>. The device writes it to
    /// <c>AddressPointer + (BlockNum - 2) * wTransferSize</c>, so blocks restart at 2 after
    /// each <see cref="SetAddress"/>.
    /// </summary>
    public sealed record WriteBlock(ushort BlockNum, ReadOnlyMemory<byte> Data) : DfuStep;

    /// <summary>
    /// Read this segment back via DFU_UPLOAD and compare to <see cref="Expected"/> — the read-back
    /// verify. Emitted after all writes and before <see cref="Leave"/> (a left device has reset and
    /// can't be read); a mismatch aborts the flash.
    /// </summary>
    public sealed record Verify(uint Address, ReadOnlyMemory<byte> Expected) : DfuStep;

    /// <summary>Leave DFU and jump to the application at <paramref name="JumpAddress"/>.</summary>
    public sealed record Leave(uint JumpAddress) : DfuStep;
}
