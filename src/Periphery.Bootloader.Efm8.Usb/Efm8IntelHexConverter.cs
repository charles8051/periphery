// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text;
using Periphery.Bootloader;
using Periphery.Firmware;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// Converts an Intel HEX firmware file into the EFM8 boot-record blob the
/// <see cref="Efm8HidProgrammer"/> flashes — so a host with a toolchain-emitted <c>.hex</c> can
/// flash an EFM8 (or a Treehopper, via the app-mode entry) end-to-end without converting the file by
/// hand. The boot-record layout depends on the part's flash map, so this is parameterized by
/// <see cref="Efm8BootOptions"/>: a composition registers it with the right part (e.g.
/// <see cref="Efm8BootOptions.Ub1"/> for a Treehopper's EFM8UB1).
/// </summary>
/// <remarks>
/// Pure (ADR-0052): a wrapper over <see cref="Efm8BootRecordGenerator.FromIntelHex"/>, whose
/// brick-safety (reset-vector-last, never-Lock, region-clipped) carries through unchanged.
/// </remarks>
public sealed class Efm8IntelHexConverter : IFirmwareConverter
{
    private readonly Efm8BootOptions _options;

    /// <summary>Creates a converter for the EFM8 part described by <paramref name="options"/> (its flash map / erase mode).</summary>
    public Efm8IntelHexConverter(Efm8BootOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public FirmwareFormat Source => FirmwareFormat.IntelHex;

    /// <inheritdoc />
    public FirmwareFormat Target => FirmwareFormat.Efm8BootRecords;

    /// <inheritdoc />
    /// <exception cref="IntelHexFormatException"><paramref name="sourceContent"/> is not well-formed Intel HEX text.</exception>
    public FirmwarePayload Convert(ReadOnlyMemory<byte> sourceContent)
    {
        var hexText = Encoding.ASCII.GetString(sourceContent.Span);
        var records = Efm8BootRecordGenerator.FromIntelHex(hexText, _options);
        return FirmwarePayload.FromBlob(records, FirmwareFormat.Efm8BootRecords);
    }
}
