// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using Periphery.Firmware;

namespace Periphery.Bootloader;

/// <summary>
/// Converts a firmware file from one format into another a target flasher accepts — the host-side
/// "conversion out" seam (image-format taxonomy). The canonical case: a byte-writing toolchain emits
/// Intel HEX, but a packaged-blob flasher (EFM8) consumes only its own boot-record stream, so a
/// converter turns the HEX into that blob before flashing. Converters are family/part-specific (the
/// EFM8 boot-record layout depends on the part's flash map), so a composition registers the right one.
/// </summary>
/// <remarks>
/// Pure (ADR-0052): a total transform over in-memory bytes, no IO. It takes the <b>raw source-file
/// bytes</b> (not a parsed image) because a packaged-blob generator needs the original representation.
/// </remarks>
public interface IFirmwareConverter
{
    /// <summary>The format this converter reads.</summary>
    FirmwareFormat Source { get; }

    /// <summary>The format this converter produces.</summary>
    FirmwareFormat Target { get; }

    /// <summary>Converts the raw bytes of a <see cref="Source"/>-format firmware file into a <see cref="Target"/> payload.</summary>
    FirmwarePayload Convert(ReadOnlyMemory<byte> sourceContent);
}

/// <summary>
/// Holds the registered <see cref="IFirmwareConverter"/>s and finds one that bridges a loaded format
/// to whatever a target's programmer accepts. Empty by default (most targets flash a directly-loadable
/// format); a device-specific composition registers the converters it needs.
/// </summary>
public sealed class FirmwareConverterRegistry
{
    private readonly List<IFirmwareConverter> _converters = new();

    /// <summary>Registers a converter. Earlier registrations win ties in <see cref="Find"/>.</summary>
    public void Register(IFirmwareConverter converter) => _converters.Add(converter);

    /// <summary>The registered converters, in registration order.</summary>
    public IReadOnlyList<IFirmwareConverter> Converters => _converters;

    /// <summary>
    /// The first converter from <paramref name="source"/> to any format in
    /// <paramref name="acceptedTargets"/>, or null if none bridges the gap.
    /// </summary>
    public IFirmwareConverter? Find(FirmwareFormat source, IEnumerable<FirmwareFormat> acceptedTargets)
    {
        ArgumentNullException.ThrowIfNull(acceptedTargets);
        var accepted = acceptedTargets as IReadOnlyCollection<FirmwareFormat> ?? acceptedTargets.ToList();
        return _converters.FirstOrDefault(c => c.Source == source && accepted.Contains(c.Target));
    }
}
