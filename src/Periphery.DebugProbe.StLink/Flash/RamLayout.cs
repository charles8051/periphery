// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.DebugProbe.Flash;

/// <summary>
/// Where a flash algorithm sits in target RAM (ADR-0094 D5): the breakpoint header, the code and
/// data, the stack, then one programming page of buffer. No two regions overlap.
/// </summary>
internal readonly record struct RamLayout(
    uint HeaderAddress,
    uint CodeAddress,
    uint StaticBase,
    uint StackTop,
    uint BufferAddress,
    uint End)
{
    /// <summary>The stack each algorithm call gets, as probe-rs gives it.</summary>
    public const uint DefaultStackSize = 512;

    /// <summary>
    /// The Thumb breakpoint header: two <c>BKPT</c> instructions. LR points here, so a
    /// returning function halts the core.
    /// </summary>
    public static ReadOnlySpan<byte> BreakpointHeader => [0x00, 0xBE, 0x00, 0xBE];

    /// <summary>Lays <paramref name="algorithm"/> out in its RAM window, or refuses when it does not fit.</summary>
    public static RamLayout For(FlashAlgorithm algorithm, uint stackSize = DefaultStackSize)
    {
        uint header = algorithm.RamStart;
        uint code = header + (uint)BreakpointHeader.Length;
        ulong stackBottom = AlignUp((ulong)code + (ulong)algorithm.Code.Length, 8);
        ulong stackTop = stackBottom + stackSize;
        ulong end = stackTop + algorithm.PageSize;
        ulong windowEnd = (ulong)algorithm.RamStart + algorithm.RamSize;

        if (end > windowEnd)
            throw new FlashPlanException(
                $"{algorithm.Name} needs {end - algorithm.RamStart} bytes of RAM for code, stack and buffer; " +
                $"its window at 0x{algorithm.RamStart:X8} has {algorithm.RamSize}.");

        return new RamLayout(header, code, code + algorithm.DataSectionOffset, (uint)stackTop, (uint)stackTop, (uint)end);
    }

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;
}

/// <summary>A flash plan refused an image or an algorithm before anything was written.</summary>
internal sealed class FlashPlanException : Exception
{
    public FlashPlanException(string message) : base(message) { }
}
