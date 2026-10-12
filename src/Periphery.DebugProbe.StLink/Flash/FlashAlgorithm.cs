// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.DebugProbe.Flash;

/// <summary>A run of equal-sized sectors from a CMSIS FlashDevice record.</summary>
/// <param name="Size">Sector size in bytes.</param>
/// <param name="Offset">Where the run starts, relative to the flash start.</param>
internal readonly record struct FlashSectorRun(uint Size, uint Offset);

/// <summary>Where a flash algorithm record came from (ADR-0094 D9).</summary>
internal sealed record FlashAlgorithmProvenance(string Pack, string Version, string Path, string Sha256);

/// <summary>
/// A CMSIS-Pack flash algorithm, generated from its FLM file (ADR-0094 D9): the code to load,
/// the function entry points, and the FlashDevice geometry. Runtime code reads these records and
/// never parses a pack.
/// </summary>
/// <remarks>
/// <c>Code</c> is PrgCode, then PrgData, then the zeroed BSS: the bytes loaded after the
/// breakpoint header. The <c>*Offset</c> entry points are offsets into <c>Code</c> with the Thumb
/// bit clear. <c>DataSectionOffset</c> is where PrgData starts in <c>Code</c>, and R9 points
/// there. <c>PageSize</c> is the chunk one <c>ProgramPage</c> call accepts; a sector is the erase
/// unit.
/// </remarks>
internal sealed record FlashAlgorithm(
    string Name,
    ReadOnlyMemory<byte> Code,
    uint InitOffset,
    uint UnInitOffset,
    uint EraseSectorOffset,
    uint ProgramPageOffset,
    uint DataSectionOffset,
    uint FlashStart,
    uint FlashSize,
    uint PageSize,
    byte ErasedValue,
    TimeSpan ProgramPageTimeout,
    TimeSpan EraseSectorTimeout,
    ImmutableArray<FlashSectorRun> Sectors,
    uint RamStart,
    uint RamSize,
    FlashAlgorithmProvenance Provenance)
{
    public uint FlashEnd => FlashStart + FlashSize;

    /// <summary>The sector holding <paramref name="address"/>, which must be inside the flash range.</summary>
    public (uint Start, uint Size) SectorContaining(uint address)
    {
        if (address < FlashStart || address >= FlashEnd)
            throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:X8} is outside {Name}.");

        uint offset = address - FlashStart;
        var run = Sectors[0];
        foreach (var candidate in Sectors)
        {
            if (candidate.Offset > offset)
                break;
            run = candidate;
        }
        uint index = (offset - run.Offset) / run.Size;
        return (FlashStart + run.Offset + index * run.Size, run.Size);
    }
}
