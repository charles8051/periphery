// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// The call convention (R0-R3 arguments, R9 static base and SP on Init, LR to a breakpoint, clock 0)
// follows probe-rs (probe-rs/src/flashing/flasher.rs, MIT OR Apache-2.0, Copyright (c) 2019
// probe-rs). See THIRD-PARTY-NOTICES.md.

using Periphery.DebugProbe.CortexM;

namespace Periphery.DebugProbe.Flash;

/// <summary>The operation code CMSIS passes to <c>Init</c> and <c>UnInit</c>.</summary>
internal enum FlashOperation : uint
{
    Erase = 1,
    Program = 2,
    Verify = 3,
}

/// <summary>One call into a flash algorithm, as the register values that make it.</summary>
internal sealed record AlgorithmCall(string Name, uint EntryOffset, uint R0, uint? R1, uint? R2, bool IsInit, TimeSpan Timeout)
{
    /// <summary>How long <c>Init</c> and <c>UnInit</c> may take. Their records carry no timeout.</summary>
    public static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(2);

    public static AlgorithmCall Init(FlashAlgorithm algorithm, FlashOperation operation) =>
        new("Init", algorithm.InitOffset, algorithm.FlashStart, 0, (uint)operation, IsInit: true, InitTimeout);

    public static AlgorithmCall UnInit(FlashAlgorithm algorithm, FlashOperation operation) =>
        new("UnInit", algorithm.UnInitOffset, (uint)operation, null, null, IsInit: false, InitTimeout);

    public static AlgorithmCall EraseSector(FlashAlgorithm algorithm, uint address) =>
        new("EraseSector", algorithm.EraseSectorOffset, address, null, null, IsInit: false, algorithm.EraseSectorTimeout);

    public static AlgorithmCall ProgramPage(FlashAlgorithm algorithm, uint address, uint size, uint buffer) =>
        new("ProgramPage", algorithm.ProgramPageOffset, address, size, buffer, IsInit: false, algorithm.ProgramPageTimeout);

    /// <summary>
    /// The core-register writes that set up this call, in order. PC goes last so a partly set up
    /// call never has a valid entry point.
    /// </summary>
    public IReadOnlyList<(CoreRegister Register, uint Value)> RegisterWrites(RamLayout layout)
    {
        var writes = new List<(CoreRegister, uint)> { (CoreRegister.R0, R0) };
        if (R1 is { } r1) writes.Add((CoreRegister.R1, r1));
        if (R2 is { } r2) writes.Add((CoreRegister.R2, r2));
        if (IsInit)
        {
            writes.Add((CoreRegister.R9, layout.StaticBase));
            writes.Add((CoreRegister.Sp, layout.StackTop));
        }
        writes.Add((CoreRegister.Lr, layout.HeaderAddress | 1));
        writes.Add((CoreRegister.Xpsr, CortexMRegisters.XpsrThumb));
        writes.Add((CoreRegister.DebugReturnAddress, (layout.CodeAddress + EntryOffset) & ~1u));
        return writes;
    }
}
