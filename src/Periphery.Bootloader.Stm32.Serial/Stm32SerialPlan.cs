// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Linq;
using Periphery.Firmware;

namespace Periphery.Bootloader.Stm32.Serial;

/// <summary>
/// Pure planner: turns a <see cref="FirmwareImage"/> + options into the ordered
/// <see cref="Stm32SerialStep"/> sequence the shell executes. No IO, no clock.
/// </summary>
/// <remarks>
/// Extended Erase over the pages the image covers, then per segment a run of
/// <see cref="Stm32SerialOptions.WriteChunkSize"/> writes at absolute addresses; then, when
/// <see cref="FlashOptions.Verify"/>, a read-back Verify per segment; then Go.
/// </remarks>
internal static class Stm32SerialPlan
{
    /// <summary>Base of STM32 internal flash, and the address page 0 starts at.</summary>
    public const uint FlashBase = 0x08000000;

    public static ImmutableArray<Stm32SerialStep> Plan(FirmwareImage image, Stm32SerialOptions serial, FlashOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serial.WriteChunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serial.ErasePageSize);

        var steps = ImmutableArray.CreateBuilder<Stm32SerialStep>();

        if (options.Erase != EraseMode.None)
        {
            int pages = PageCountToCover(image, serial.ErasePageSize);
            if (pages > 0)
                steps.Add(new Stm32SerialStep.ErasePages(pages));
        }

        foreach (var segment in image.Segments)
        {
            if (segment.Data.Length == 0)
                continue;

            // Each chunk carries its own absolute address, so a sparse multi-region image lands
            // where it belongs. The planner never relocates a segment.
            for (int offset = 0; offset < segment.Data.Length; offset += serial.WriteChunkSize)
            {
                int length = Math.Min(serial.WriteChunkSize, segment.Data.Length - offset);
                steps.Add(new Stm32SerialStep.Write(
                    segment.Address + (uint)offset,
                    segment.Data.Slice(offset, length)));
            }
        }

        if (options.Verify)
        {
            foreach (var segment in image.Segments)
                if (segment.Data.Length > 0)
                    steps.Add(new Stm32SerialStep.Verify(segment.Address, segment.Data));
        }

        if (options.LeaveAfterFlash)
        {
            // Jump to the image's lowest address (its vector table / entry point).
            uint jump = image.Segments.IsDefaultOrEmpty
                ? FlashBase
                : image.Segments.Min(s => s.Address);
            steps.Add(new Stm32SerialStep.Go(jump));
        }

        return steps.ToImmutable();
    }

    /// <summary>
    /// Pages from page 0 through the last page the image touches. Erase always starts at page 0
    /// (see <see cref="Stm32SerialStep.ErasePages"/>), so this is a count, not a window: an image
    /// at 0x08008000 with a 2 KiB page still erases pages 0..16.
    /// </summary>
    internal static int PageCountToCover(FirmwareImage image, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        if (image.Segments.IsDefaultOrEmpty)
            return 0;

        uint end = 0;
        foreach (var segment in image.Segments)
        {
            if (segment.Data.Length == 0)
                continue;
            uint segmentEnd = segment.Address + (uint)segment.Data.Length;
            if (segmentEnd > end)
                end = segmentEnd;
        }

        if (end <= FlashBase)
            return 0;

        long span = end - FlashBase;
        return (int)((span + pageSize - 1) / pageSize);
    }
}
