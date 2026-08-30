// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// Pure parse of a USB configuration-descriptor blob for the DFU functional descriptor
/// (<c>bDescriptorType = 0x21</c>) and its <c>wTransferSize</c> field (bytes 5-6,
/// little-endian). The shell fetches the blob via GET_DESCRIPTOR and calls this; a
/// constant fallback covers parse failure (ADR-0061).
/// </summary>
internal static class DfuFunctionalDescriptor
{
    private const byte DfuFunctionalDescriptorType = 0x21;

    /// <summary>
    /// Walks the descriptor blob (each entry is <c>bLength, bDescriptorType, …</c>) and,
    /// at the DFU functional descriptor, reads <c>wTransferSize</c>.
    /// </summary>
    public static bool TryParseTransferSize(ReadOnlySpan<byte> configDescriptor, out int transferSize)
    {
        transferSize = 0;
        int i = 0;
        while (i + 2 <= configDescriptor.Length)
        {
            int bLength = configDescriptor[i];
            if (bLength < 2 || i + bLength > configDescriptor.Length)
                break; // malformed / truncated

            if (configDescriptor[i + 1] == DfuFunctionalDescriptorType && bLength >= 7)
            {
                transferSize = configDescriptor[i + 5] | (configDescriptor[i + 6] << 8);
                return transferSize > 0;
            }

            i += bLength;
        }

        return false;
    }
}
