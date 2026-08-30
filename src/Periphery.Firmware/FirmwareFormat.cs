// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;

namespace Periphery.Firmware;

/// <summary>
/// The two kinds of firmware file, the split that decides how a flasher consumes it (firmware
/// image-format taxonomy, <c>docs/feature-specs/firmware-flashing/image-formats/</c>).
/// </summary>
public enum FirmwareKind
{
    /// <summary>Decomposes to <c>address -&gt; bytes</c>: a byte-writing bootloader's input. All collapse to <see cref="FirmwareImage"/>.</summary>
    MemoryImage,

    /// <summary>A packaged / protocol-native blob carrying its own structure (the bootloader's native format), consumed as-is — not re-addressed.</summary>
    PackagedBlob,
}

/// <summary>
/// A firmware file format. The Kind-1 (memory image) values all reduce to a
/// <see cref="FirmwareImage"/>; the Kind-2 (packaged blob) values are consumed as-is by their
/// family's flasher. This is the subset in use today; it grows as formats land (SRecord, DfuSe,
/// GBL, ...).
/// </summary>
public enum FirmwareFormat
{
    /// <summary>Intel HEX text (<c>.hex</c>, leading <c>:</c>). Kind 1.</summary>
    IntelHex,

    /// <summary>Raw binary (<c>.bin</c>, base supplied at load). Kind 1.</summary>
    RawBinary,

    /// <summary>ELF (<c>.elf</c>/<c>.axf</c>/<c>.out</c>, <c>\x7FELF</c> magic), its <c>PT_LOAD</c> segments. Kind 1.</summary>
    Elf,

    /// <summary>EFM8 AN945 boot-record stream (<c>.efm8</c>/<c>.tfi</c>, leading <c>$</c>), replayed as-is. Kind 2.</summary>
    Efm8BootRecords,
}

/// <summary>
/// Format registry helpers. Today: the kind of each format and extension-based detection. The
/// content-sniff + reconciling <c>Detect</c> registry (image-formats spec, Layer 1) folds in here
/// when it lands; <see cref="FromExtension"/> is forward-compatible with it.
/// </summary>
public static class FirmwareFormats
{
    /// <summary>The <see cref="FirmwareKind"/> of <paramref name="format"/>.</summary>
    public static FirmwareKind Kind(this FirmwareFormat format) => format switch
    {
        FirmwareFormat.IntelHex or FirmwareFormat.RawBinary or FirmwareFormat.Elf => FirmwareKind.MemoryImage,
        FirmwareFormat.Efm8BootRecords => FirmwareKind.PackagedBlob,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown firmware format."),
    };

    /// <summary>
    /// The format implied by <paramref name="fileName"/>'s extension, or <c>null</c> if unrecognized.
    /// Extension-only — the content sniff that confirms it (the brick-guard) lives in the parsers /
    /// the future shared registry.
    /// </summary>
    public static FirmwareFormat? FromExtension(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".hex" or ".ihex" => FirmwareFormat.IntelHex,
            ".bin" => FirmwareFormat.RawBinary,
            ".elf" or ".axf" or ".out" => FirmwareFormat.Elf,
            ".efm8" or ".tfi" => FirmwareFormat.Efm8BootRecords,
            _ => null,
        };
    }
}
