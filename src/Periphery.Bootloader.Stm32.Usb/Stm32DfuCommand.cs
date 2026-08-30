// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// An ST bootloader command carried in a DFU_DNLOAD with <c>wBlockNum = 0</c> (AN3156 §5).
/// The first payload byte is the command code; the rest is command data. <see cref="Encode"/>
/// produces that payload. Pure; the ST-command layer above generic DFU (ADR-0061 DEC-005).
/// </summary>
internal abstract record Stm32DfuCommand
{
    private protected Stm32DfuCommand() { }

    /// <summary>The <c>wValue = 0</c> DNLOAD payload (command byte + data).</summary>
    public abstract byte[] Encode();

    /// <summary>Set Address Pointer (0x21) + 32-bit little-endian address (AN3156 §5.2).</summary>
    public sealed record SetAddress(uint Address) : Stm32DfuCommand
    {
        public override byte[] Encode() =>
            [0x21, (byte)Address, (byte)(Address >> 8), (byte)(Address >> 16), (byte)(Address >> 24)];
    }

    /// <summary>Page Erase (0x41) + 32-bit little-endian page address (AN3156 §5.3).</summary>
    public sealed record ErasePage(uint Address) : Stm32DfuCommand
    {
        public override byte[] Encode() =>
            [0x41, (byte)Address, (byte)(Address >> 8), (byte)(Address >> 16), (byte)(Address >> 24)];
    }

    /// <summary>Mass Erase: the Erase command (0x41) with no address (AN3156 §5.3).</summary>
    public sealed record MassErase : Stm32DfuCommand
    {
        public static readonly MassErase Instance = new();
        public override byte[] Encode() => [0x41];
    }

    /// <summary>Read Unprotect (0x92) (AN3156 §5.4). Destructive: mass-erase + RDP regression.</summary>
    public sealed record ReadUnprotect : Stm32DfuCommand
    {
        public static readonly ReadUnprotect Instance = new();
        public override byte[] Encode() => [0x92];
    }
}
