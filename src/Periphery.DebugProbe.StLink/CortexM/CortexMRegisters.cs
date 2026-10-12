// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.DebugProbe.CortexM;

/// <summary>A core register as the debug core-register selector (DCRSR.REGSEL) numbers it.</summary>
internal enum CoreRegister : uint
{
    R0 = 0, R1 = 1, R2 = 2, R3 = 3, R4 = 4, R5 = 5, R6 = 6, R7 = 7,
    R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12,
    Sp = 13,
    Lr = 14,

    /// <summary>The address the core resumes from; the PC while halted.</summary>
    DebugReturnAddress = 15,
    Xpsr = 16,
}

/// <summary>
/// The Armv7-M and Armv8-M debug registers this tier uses, and pure encoders and decoders for
/// them (Armv7-M Architecture Reference Manual, C1.6).
/// </summary>
internal static class CortexMRegisters
{
    public const uint Aircr = 0xE000ED0C;
    public const uint Dhcsr = 0xE000EDF0;
    public const uint Dcrsr = 0xE000EDF4;
    public const uint Dcrdr = 0xE000EDF8;
    public const uint Demcr = 0xE000EDFC;

    private const uint DbgKey = 0xA05Fu << 16;
    private const uint CDebugEn = 1u << 0;
    private const uint CHalt = 1u << 1;
    private const uint SRegRdy = 1u << 16;
    private const uint SHalt = 1u << 17;
    private const uint SLockup = 1u << 19;
    private const uint SResetSt = 1u << 25;
    private const uint RegWnR = 1u << 16;
    private const uint VcCoreReset = 1u << 0;
    private const uint VectKey = 0x05FAu << 16;
    private const uint SysResetReq = 1u << 2;

    /// <summary>xPSR with only the Thumb bit set, which every Cortex-M call needs.</summary>
    public const uint XpsrThumb = 1u << 24;

    /// <summary>DHCSR value that enables debug and halts the core.</summary>
    public const uint DhcsrHalt = DbgKey | CDebugEn | CHalt;

    /// <summary>DHCSR value that keeps debug enabled and lets the core run.</summary>
    public const uint DhcsrRun = DbgKey | CDebugEn;

    /// <summary>DHCSR value that turns halting debug off, so a reset runs the application freely.</summary>
    public const uint DhcsrDetach = DbgKey;

    /// <summary>AIRCR value that requests a system reset.</summary>
    public const uint AircrSystemReset = VectKey | SysResetReq;

    public static bool IsHalted(uint dhcsr) => (dhcsr & SHalt) != 0;

    public static bool IsRegisterReady(uint dhcsr) => (dhcsr & SRegRdy) != 0;

    public static bool IsLockedUp(uint dhcsr) => (dhcsr & SLockup) != 0;

    /// <summary>DHCSR.S_RESET_ST: a reset happened since DHCSR was last read.</summary>
    public static bool ResetSinceLastRead(uint dhcsr) => (dhcsr & SResetSt) != 0;

    public static uint DcrsrWrite(CoreRegister register) => (uint)register | RegWnR;

    public static uint DcrsrRead(CoreRegister register) => (uint)register;

    /// <summary>DEMCR with reset vector catch set or cleared, other bits kept.</summary>
    public static uint WithResetCatch(uint demcr, bool enabled) =>
        enabled ? demcr | VcCoreReset : demcr & ~VcCoreReset;
}
