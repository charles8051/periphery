// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Firmware;

/// <summary>
/// What a flasher is handed to write: either a Kind-1 <b>memory image</b> (addressed
/// <see cref="FirmwareSegment"/>s, for a byte-writing bootloader) or a Kind-2 <b>packaged blob</b>
/// (a protocol-native byte stream consumed as-is, e.g. an EFM8 boot-record stream). The single
/// payload type both flasher families accept, tagged with the <see cref="FirmwareFormat"/> so a
/// programmer can refuse a format it does not handle (the safety gate, ADR-0061 / ADR-0063).
/// </summary>
/// <remarks>
/// Pure value (ADR-0052). A memory-image payload carries the parsed <see cref="FirmwareImage"/>; a
/// packaged-blob payload carries the raw bytes. Exactly one is populated, determined by
/// <see cref="Format"/>'s <see cref="FirmwareKind"/>.
/// </remarks>
public sealed class FirmwarePayload
{
    private readonly ReadOnlyMemory<byte> _blob;

    private FirmwarePayload(FirmwareFormat format, FirmwareImage? memoryImage, ReadOnlyMemory<byte> blob)
    {
        Format = format;
        MemoryImage = memoryImage;
        _blob = blob;
    }

    /// <summary>The format this payload is in.</summary>
    public FirmwareFormat Format { get; }

    /// <summary>Whether this is a memory image or a packaged blob (derived from <see cref="Format"/>).</summary>
    public FirmwareKind Kind => Format.Kind();

    /// <summary>The addressed memory image when <see cref="Kind"/> is <see cref="FirmwareKind.MemoryImage"/>; otherwise <c>null</c>.</summary>
    public FirmwareImage? MemoryImage { get; }

    /// <summary>The packaged-blob bytes when <see cref="Kind"/> is <see cref="FirmwareKind.PackagedBlob"/>; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Blob => _blob;

    /// <summary>Total payload bytes — the memory image's, or the blob's — for sizing and progress.</summary>
    public long ByteLength => Kind == FirmwareKind.MemoryImage ? MemoryImage?.TotalBytes ?? 0 : _blob.Length;

    /// <summary>A Kind-1 memory-image payload.</summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not a memory-image format.</exception>
    public static FirmwarePayload FromImage(FirmwareImage image, FirmwareFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (format.Kind() != FirmwareKind.MemoryImage)
            throw new ArgumentException($"{format} is not a memory-image format.", nameof(format));
        return new FirmwarePayload(format, image, default);
    }

    /// <summary>A Kind-2 packaged-blob payload of <paramref name="blob"/> bytes, consumed as-is by its family's flasher.</summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not a packaged-blob format.</exception>
    public static FirmwarePayload FromBlob(ReadOnlyMemory<byte> blob, FirmwareFormat format)
    {
        if (format.Kind() != FirmwareKind.PackagedBlob)
            throw new ArgumentException($"{format} is not a packaged-blob format.", nameof(format));
        return new FirmwarePayload(format, memoryImage: null, blob);
    }

    /// <summary>
    /// Loads a payload from a firmware file, inferring the format from <paramref name="fileName"/>'s
    /// extension. A <b>memory-image</b> format is parsed via <see cref="FirmwareImage.Load"/> (with
    /// its content brick-guard). A <b>packaged-blob</b> format (e.g. EFM8 boot records) is taken
    /// as-is — its family flasher validates it before writing a byte (so a mislabelled blob is
    /// refused at flash time, before any device IO), and no memory-image parse applies.
    /// </summary>
    /// <exception cref="FirmwareFormatException">The extension is unrecognized, or a memory-image's content does not match it.</exception>
    public static FirmwarePayload Load(ReadOnlyMemory<byte> content, string fileName, uint binBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var format = FirmwareFormats.FromExtension(fileName);
        if (format is null)
            throw new FirmwareFormatException(
                $"Cannot determine the firmware format of '{fileName}' from its extension. " +
                "Use .bin (raw binary), .hex (Intel HEX), .elf (ELF), or .efm8/.tfi (EFM8 boot records).");

        if (format.Value.Kind() == FirmwareKind.PackagedBlob)
            return FromBlob(content, format.Value);

        var image = FirmwareImage.Load(content, fileName, binBaseAddress);
        return FromImage(image, format.Value);
    }
}
