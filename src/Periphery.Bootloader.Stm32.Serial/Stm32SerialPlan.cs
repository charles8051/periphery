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

    /// <summary>
    /// Most pages one Extended Erase can address. The command carries the page count as a
    /// half-word, and AN3155 §3.7 reserves the values from 0xFFFD upward for mass and bank erase,
    /// so the largest usable count is 0xFFF0 with headroom below the reserved range.
    /// </summary>
    public const int MaxErasePages = 0xFFF0;

    public static ImmutableArray<Stm32SerialStep> Plan(FirmwareImage image, Stm32SerialOptions serial, FlashOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serial.WriteChunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serial.ErasePageSize);

        ValidateSegments(image);

        var steps = ImmutableArray.CreateBuilder<Stm32SerialStep>();

        if (options.Erase == EraseMode.Mass)
        {
            // One command, no page list, and it clears flash the image does not cover. Only ever
            // on an explicit request: Auto stays a page erase, which is the "better" the mode's
            // own documentation offers.
            steps.Add(new Stm32SerialStep.EraseAll());
        }
        else if (options.Erase != EraseMode.None)
        {
            int pages = PageCountToCover(image, serial.ErasePageSize);

            // Refuse rather than truncate. The shell narrows this count to a ushort for the wire,
            // so an out-of-range value would wrap silently and erase an arbitrary, unrelated number
            // of pages — and the nearest wrap is 0, which erases one page and then writes into
            // un-erased flash. Verify would catch that; --no-verify would not.
            if (pages > MaxErasePages)
                throw new Stm32SerialException(
                    $"the image reaches 0x{FlashBase + (uint)((long)pages * serial.ErasePageSize):X8}, " +
                    $"which is {pages} pages of {serial.ErasePageSize} bytes above 0x{FlashBase:X8} — " +
                    $"more than the {MaxErasePages} an Extended Erase can address. " +
                    "Usually this means a segment outside main flash (option bytes or system memory), " +
                    "or a --base that does not match the part. Erase separately and flash with EraseMode.None.");

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
    /// Refuses an image the planner cannot describe safely, before any step is emitted.
    /// <para>
    /// Write Memory reaches system memory, SRAM and the option bytes as readily as it reaches
    /// flash, and a bad option-byte write is the one operation on this part that is not
    /// recoverable from outside. The erase guard alone does not cover it: an image entirely below
    /// <see cref="FlashBase"/> produces a page count of zero, skips the erase step, and would
    /// still have its writes emitted at whatever address it named.
    /// </para>
    /// <para>
    /// There is no chip database yet, so there is no upper bound to check against — the erase
    /// page count is the only ceiling. This checks the half that does not need one.
    /// </para>
    /// </summary>
    private static void ValidateSegments(FirmwareImage image)
    {
        foreach (var segment in image.Segments)
        {
            if (segment.Data.Length == 0)
                continue;

            if (segment.Address < FlashBase)
                throw new Stm32SerialException(
                    $"the image has a segment at 0x{segment.Address:X8}, below the flash base " +
                    $"0x{FlashBase:X8}. Writing there reaches system memory, SRAM or the option bytes " +
                    "rather than the application region. Check the image's base address.");

            // Deliberately in ulong: the end of a segment near the top of the address space wraps
            // in uint arithmetic, and a wrapped end looks small enough to slip past the page-count
            // guard below.
            ulong end = (ulong)segment.Address + (ulong)segment.Data.Length;
            if (end > (ulong)uint.MaxValue + 1)
                throw new Stm32SerialException(
                    $"the image has a segment at 0x{segment.Address:X8} of {segment.Data.Length} bytes, " +
                    "which runs past the end of the 32-bit address space.");
        }
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

        // ulong throughout: uint arithmetic wraps at the top of the address space, and a wrapped
        // end reads as a small one, which is exactly how an out-of-range image would slip past the
        // page-count ceiling. ValidateSegments rejects that case first; this does not rely on it.
        ulong end = 0;
        foreach (var segment in image.Segments)
        {
            if (segment.Data.Length == 0)
                continue;
            ulong segmentEnd = (ulong)segment.Address + (ulong)segment.Data.Length;
            if (segmentEnd > end)
                end = segmentEnd;
        }

        if (end <= FlashBase)
            return 0;

        ulong span = end - FlashBase;
        ulong pages = (span + (ulong)pageSize - 1) / (ulong)pageSize;
        return pages > int.MaxValue ? int.MaxValue : (int)pages;
    }
}
