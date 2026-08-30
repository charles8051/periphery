// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Linq;
using Periphery.Firmware;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// Pure planner: turns a <see cref="FirmwareImage"/> + transfer size + options into the
/// ordered <see cref="DfuStep"/> sequence the shell executes. No IO, no clock.
/// </summary>
/// <remarks>
/// Mass-erase, then per segment a Set-Address-Pointer followed by <c>wTransferSize</c>-sized write
/// blocks (numbered from 2); then, when <see cref="FlashOptions.Verify"/>, a read-back Verify per
/// segment; then Leave. Per-page erase (needs the DfuSe memory-layout descriptor) is still phase 2.
/// </remarks>
internal static class Stm32DfuPlan
{
    public static ImmutableArray<DfuStep> Plan(FirmwareImage image, int transferSize, FlashOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transferSize);

        var steps = ImmutableArray.CreateBuilder<DfuStep>();

        if (options.Erase != EraseMode.None)
            steps.Add(DfuStep.MassErase.Instance); // phase 1: mass erase only

        foreach (var segment in image.Segments)
        {
            if (segment.Data.Length == 0)
                continue;

            // Each segment is written at its own absolute address (Intel HEX carries them; a
            // raw binary was placed at its base when loaded). The planner never overrides them.
            steps.Add(new DfuStep.SetAddress(segment.Address));

            ushort block = 2; // wBlockNum 0/1 are reserved; data blocks start at 2 (AN3156 §5.1)
            for (int offset = 0; offset < segment.Data.Length; offset += transferSize)
            {
                int length = Math.Min(transferSize, segment.Data.Length - offset);
                steps.Add(new DfuStep.WriteBlock(block, segment.Data.Slice(offset, length)));
                block++;
            }
        }

        if (options.Verify)
        {
            // Read-back verify after the whole image is written, before leaving (a left device has
            // already reset and can't be read). Each non-empty segment is uploaded and compared.
            foreach (var segment in image.Segments)
                if (segment.Data.Length > 0)
                    steps.Add(new DfuStep.Verify(segment.Address, segment.Data));
        }

        if (options.LeaveAfterFlash)
        {
            // Jump to the application's lowest address (its vector table / entry point).
            uint jump = image.Segments.IsDefaultOrEmpty
                ? 0x08000000u
                : image.Segments.Min(s => s.Address);
            steps.Add(new DfuStep.Leave(jump));
        }

        return steps.ToImmutable();
    }
}
