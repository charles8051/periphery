// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Firmware;

namespace Periphery.DebugProbe.Flash;

/// <summary>A sector and the bytes it holds now.</summary>
internal sealed record SectorContents(uint Address, ReadOnlyMemory<byte> Data);

/// <summary>A sector's bytes after the image is merged in, and whether they differ from now.</summary>
internal sealed record MergedSector(uint Address, ReadOnlyMemory<byte> Data, bool Changed);

/// <summary>One step of a flash: a call into the algorithm, or a write of the page buffer.</summary>
internal abstract record FlashStep
{
    private FlashStep() { }

    public sealed record Call(AlgorithmCall Algorithm) : FlashStep;

    /// <summary>Copy one programming page into the RAM buffer before its <c>ProgramPage</c> call.</summary>
    public sealed record WriteBuffer(uint Address, ReadOnlyMemory<byte> Data) : FlashStep;
}

/// <summary>
/// The pure flash plan (ADR-0094 D5 and D6). It decides which sectors to read, merges the image
/// into them so bytes the image does not cover keep their values, and lists the algorithm calls
/// that erase and program the sectors that changed. Every refusal happens here, before any write.
/// </summary>
internal static class FlashPlan
{
    /// <summary>
    /// The sectors <paramref name="image"/> touches, in address order. Refuses a segment outside
    /// the flash range, which is the algorithm's range cut to the size the part reports.
    /// </summary>
    public static IReadOnlyList<(uint Address, uint Size)> SectorsToRead(
        FirmwareImage image, FlashAlgorithm algorithm, uint reportedFlashSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(algorithm);

        ulong end = algorithm.FlashStart + (ulong)Math.Min(algorithm.FlashSize, reportedFlashSize);
        if (image.Segments.IsEmpty)
            throw new FlashPlanException("The image is empty.");

        var sectors = new SortedDictionary<uint, uint>();
        foreach (var segment in image.Segments)
        {
            if (segment.Data.IsEmpty)
                continue;
            ulong segmentEnd = segment.Address + (ulong)segment.Data.Length;
            if (segment.Address < algorithm.FlashStart || segmentEnd > end)
                throw new FlashPlanException(
                    $"The image writes 0x{segment.Address:X8}..0x{segmentEnd - 1:X8}, outside flash " +
                    $"0x{algorithm.FlashStart:X8}..0x{end - 1:X8}.");

            uint at = segment.Address;
            while (at < segmentEnd)
            {
                var (start, size) = algorithm.SectorContaining(at);
                sectors[start] = size;
                at = start + size;
            }
        }

        if (sectors.Count == 0)
            throw new FlashPlanException("The image is empty.");
        return sectors.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Overlays the image on each sector's current bytes. Bytes the image does not cover keep
    /// their values.
    /// </summary>
    public static IReadOnlyList<MergedSector> Merge(IReadOnlyList<SectorContents> current, FirmwareImage image)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(image);

        var merged = new List<MergedSector>(current.Count);
        foreach (var sector in current)
        {
            var bytes = sector.Data.ToArray();
            ulong sectorEnd = sector.Address + (ulong)bytes.Length;
            foreach (var segment in image.Segments)
            {
                ulong segmentEnd = segment.Address + (ulong)segment.Data.Length;
                ulong from = Math.Max(sector.Address, segment.Address);
                ulong to = Math.Min(sectorEnd, segmentEnd);
                if (from >= to)
                    continue;
                segment.Data.Span.Slice((int)(from - segment.Address), (int)(to - from))
                    .CopyTo(bytes.AsSpan((int)(from - sector.Address)));
            }
            merged.Add(new MergedSector(sector.Address, bytes, !bytes.AsSpan().SequenceEqual(sector.Data.Span)));
        }
        return merged;
    }

    /// <summary>
    /// The calls that erase every changed sector, then program its pages. A page left at the
    /// erased value is not programmed. An unchanged sector is neither erased nor programmed.
    /// </summary>
    public static IReadOnlyList<FlashStep> Steps(IReadOnlyList<MergedSector> merged, FlashAlgorithm algorithm, RamLayout layout)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(algorithm);

        var changed = merged.Where(s => s.Changed).ToList();
        if (changed.Count == 0)
            return [];

        var steps = new List<FlashStep> { new FlashStep.Call(AlgorithmCall.Init(algorithm, FlashOperation.Erase)) };
        foreach (var sector in changed)
            steps.Add(new FlashStep.Call(AlgorithmCall.EraseSector(algorithm, sector.Address)));
        steps.Add(new FlashStep.Call(AlgorithmCall.UnInit(algorithm, FlashOperation.Erase)));

        steps.Add(new FlashStep.Call(AlgorithmCall.Init(algorithm, FlashOperation.Program)));
        int pageSize = (int)algorithm.PageSize;
        foreach (var sector in changed)
        {
            if (sector.Data.Length % pageSize != 0)
                throw new FlashPlanException(
                    $"{algorithm.Name}: a {sector.Data.Length}-byte sector is not a whole number of {pageSize}-byte pages.");

            for (int offset = 0; offset < sector.Data.Length; offset += pageSize)
            {
                var page = sector.Data.Slice(offset, pageSize);
                if (IsErased(page.Span, algorithm.ErasedValue))
                    continue;
                uint address = sector.Address + (uint)offset;
                steps.Add(new FlashStep.WriteBuffer(layout.BufferAddress, page));
                steps.Add(new FlashStep.Call(
                    AlgorithmCall.ProgramPage(algorithm, address, (uint)pageSize, layout.BufferAddress)));
            }
        }
        steps.Add(new FlashStep.Call(AlgorithmCall.UnInit(algorithm, FlashOperation.Program)));
        return steps;
    }

    private static bool IsErased(ReadOnlySpan<byte> page, byte erasedValue) =>
        page.IndexOfAnyExcept(erasedValue) < 0;
}
