// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;
using System.Text;
using Periphery.Firmware;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>The firmware-file formats the uploader can consume.</summary>
public enum Efm8FirmwareFormat
{
    /// <summary>Intel HEX text (a raw firmware image; converted to boot records before upload).</summary>
    IntelHex,

    /// <summary>A <c>$</c>-framed AN945 boot-record stream (<c>.efm8</c> / <c>.tfi</c>), uploaded as-is.</summary>
    BootRecords,
}

/// <summary>
/// Resolves an arbitrary firmware file into the boot-record stream the bootloader
/// uploader replays, with a <b>brick-safety guard</b>: the format is inferred from the
/// file extension and then <em>verified against the actual content</em>, so a wrong or
/// mislabelled file is rejected before a single byte can reach the device.
/// </summary>
/// <remarks>
/// <para>Pure and total (ADR-0052): no IO, no device. <see cref="ToBootRecords"/> runs
/// entirely on the in-memory file bytes, so any refusal happens while the board is still
/// safely running its current firmware.</para>
/// <para>The dangerous case this exists to stop: an Intel HEX file (ASCII text) handed to
/// the uploader as if it were a boot-record stream would stream HEX characters at the
/// bootloader as flash commands. Here a <c>.tfi</c>/<c>.efm8</c> whose content is actually
/// Intel HEX (or vice versa) is refused outright.</para>
/// </remarks>
public static class Efm8FirmwareImage
{
    /// <summary>
    /// The format implied by <paramref name="fileName"/>'s extension
    /// (<c>.hex</c> ⇒ <see cref="Efm8FirmwareFormat.IntelHex"/>;
    /// <c>.tfi</c>/<c>.efm8</c> ⇒ <see cref="Efm8FirmwareFormat.BootRecords"/>),
    /// or <see langword="null"/> if the extension is not recognized.
    /// </summary>
    public static Efm8FirmwareFormat? FormatFromFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".hex" => Efm8FirmwareFormat.IntelHex,
            ".tfi" or ".efm8" => Efm8FirmwareFormat.BootRecords,
            _ => null,
        };
    }

    /// <summary>
    /// The format suggested by <paramref name="content"/>'s leading bytes — Intel HEX
    /// begins with a <c>':'</c> record mark, a boot-record stream with <c>'$'</c>
    /// (<see cref="Efm8Protocol.StartByte"/>). <see langword="null"/> if it is neither.
    /// A cheap discriminator, not a full validation (that is <see cref="IntelHexImage.Parse"/>
    /// / <see cref="Efm8Protocol.ParseRecords"/>).
    /// </summary>
    public static Efm8FirmwareFormat? Sniff(ReadOnlySpan<byte> content)
    {
        int i = 0;
        while (i < content.Length && content[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        if (i >= content.Length) return null;
        return content[i] switch
        {
            (byte)':' => Efm8FirmwareFormat.IntelHex,
            Efm8Protocol.StartByte => Efm8FirmwareFormat.BootRecords,
            _ => null,
        };
    }

    /// <summary>
    /// Resolves <paramref name="content"/> (the raw bytes of a firmware file named
    /// <paramref name="fileName"/>) into a boot-record stream ready to upload: the format
    /// is inferred from the extension, <b>verified against the content</b>, and an Intel
    /// HEX image is converted via <see cref="Efm8BootRecordGenerator"/> using
    /// <paramref name="hexOptions"/>. A boot-record file is validated and returned as-is.
    /// </summary>
    /// <exception cref="Efm8BootFormatException">
    /// The extension is unrecognized, the content does not match the extension (the
    /// brick-guard), or the content is malformed for its format. Thrown before any device IO.
    /// </exception>
    public static byte[] ToBootRecords(ReadOnlyMemory<byte> content, string fileName, Efm8BootOptions hexOptions)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        Efm8FirmwareFormat declared = FormatFromFileName(fileName)
            ?? throw new Efm8BootFormatException(
                $"Unrecognized firmware file extension for '{fileName}'. Expected .hex (Intel HEX) " +
                "or .tfi / .efm8 (a boot-record stream).");

        Efm8FirmwareFormat? sniffed = Sniff(content.Span);
        if (sniffed != declared)
            throw new Efm8BootFormatException(
                $"Refusing to flash '{fileName}': its extension indicates {Describe(declared)}, but " +
                $"the file content looks like {Describe(sniffed)}. A mismatched file could brick the " +
                "board — rename it or re-export the correct format.");

        return declared switch
        {
            Efm8FirmwareFormat.IntelHex =>
                Efm8BootRecordGenerator.FromIntelHex(Encoding.ASCII.GetString(content.Span), hexOptions),
            // Validate the stream parses before it is ever sent to a device, then pass through.
            _ => ValidatedBootRecords(content),
        };
    }

    private static byte[] ValidatedBootRecords(ReadOnlyMemory<byte> content)
    {
        Efm8Protocol.ParseRecords(content);   // throws Efm8BootFormatException if malformed
        return content.ToArray();
    }

    private static string Describe(Efm8FirmwareFormat? format) => format switch
    {
        Efm8FirmwareFormat.IntelHex => "Intel HEX text (it starts with ':')",
        Efm8FirmwareFormat.BootRecords => "a boot-record stream (it starts with '$')",
        _ => "neither Intel HEX nor a boot-record stream",
    };
}
