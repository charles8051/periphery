// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.DebugProbe;

/// <summary>
/// The memory seam every probe-independent layer is written against (ADR-0094 D2): 32-bit reads
/// and writes of target memory. Halt, reset and core-register access are memory operations on
/// the Cortex-M debug registers, so nothing above this seam knows which probe is attached.
/// </summary>
internal interface ITargetMemory
{
    /// <summary>Reads one 32-bit word. <paramref name="address"/> is word-aligned.</summary>
    Task<uint> ReadWordAsync(uint address, CancellationToken ct);

    /// <summary>Writes one 32-bit word. <paramref name="address"/> is word-aligned.</summary>
    Task WriteWordAsync(uint address, uint value, CancellationToken ct);

    /// <summary>
    /// Reads <paramref name="destination"/>'s length in bytes. The address is word-aligned and
    /// the length a multiple of 4.
    /// </summary>
    Task ReadAsync(uint address, Memory<byte> destination, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="data"/>. The address is word-aligned and the length a multiple of 4.
    /// </summary>
    Task WriteAsync(uint address, ReadOnlyMemory<byte> data, CancellationToken ct);
}
