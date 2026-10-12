// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Frozen;
using Periphery.DebugProbe.Flash;

namespace Periphery.DebugProbe.Targets;

/// <summary>An STM32 read-protection level, from FLASH_OPTR.RDP.</summary>
internal enum ReadProtection
{
    Level0,
    Level1,
    Level2,
}

/// <summary>
/// The STM32G4 registers identification reads, and pure decoders for them. Addresses and fields
/// are from ST's STM32G431 SVD in <c>Open-CMSIS-Pack/STM32G4xx_DFP</c>; the flash-size register is
/// where the pack's flash algorithm reads it.
/// </summary>
internal static class Stm32Registers
{
    public const uint DbgmcuIdcode = 0xE0042000;

    /// <summary>DBGMCU_APB1FZR1 (SVD name APB1L_FZ).</summary>
    public const uint DbgmcuApb1Freeze = 0xE0042008;

    /// <summary>DBG_WWDG_STOP (bit 11) and DBG_IWDG_STOP (bit 12).</summary>
    public const uint WatchdogStopBits = (1u << 11) | (1u << 12);

    public const uint FlashOptr = 0x40022020;

    /// <summary>The flash size in KB, in the low half-word.</summary>
    public const uint FlashSizeData = 0x1FFF75E0;

    public static ushort DeviceId(uint idcode) => (ushort)(idcode & 0xFFF);

    public static ushort RevisionId(uint idcode) => (ushort)(idcode >> 16);

    public static uint FlashSizeBytes(uint flashSizeData) => (flashSizeData & 0xFFFF) * 1024;

    /// <summary>RDP 0xAA is level 0 and 0xCC is level 2. Every other value is level 1.</summary>
    public static ReadProtection ReadProtectionOf(uint optr) => (optr & 0xFF) switch
    {
        0xAA => ReadProtection.Level0,
        0xCC => ReadProtection.Level2,
        _ => ReadProtection.Level1,
    };
}

/// <summary>A part this tier has passed on hardware, and the algorithm that programs it.</summary>
internal sealed record Stm32Target(ushort DeviceId, string Family, string Chip, FlashAlgorithm Algorithm);

/// <summary>
/// The device-ID allowlist (ADR-0094 D5). A device ID joins only after its hardware gate passes.
/// </summary>
internal static class Stm32Catalog
{
    public static readonly FrozenDictionary<ushort, Stm32Target> Targets = new Stm32Target[]
    {
        // DEV_ID 0x468 is the STM32G431 and STM32G441; the pack gives both this algorithm.
        // Passed on a NUCLEO-G431KB through its STLINK-V3E.
        new(0x468, "STM32G4", "STM32G431/G441", FlashAlgorithms.Stm32G43x4x128),
    }.ToFrozenDictionary(t => t.DeviceId);
}
