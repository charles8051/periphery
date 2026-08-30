// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Bootloader;

/// <summary>
/// What a programmer reports about a target after <see cref="IFirmwareProgrammer.IdentifyAsync"/>:
/// family, resolved chip, bootloader version, max transfer size, the flashable memory
/// map, and the discovered command set (some protocols vary commands per part).
/// </summary>
public sealed record DeviceIdentity(
    string Family,
    string? Chip,
    string? BootloaderVersion,
    int TransferSize,
    ImmutableArray<MemoryRegion> Regions,
    ImmutableArray<string> SupportedCommands)
{
    /// <summary>A minimal identity carrying only the family (capabilities not yet read).</summary>
    public static DeviceIdentity Unknown(string family) =>
        new(family, null, null, 0, ImmutableArray<MemoryRegion>.Empty, ImmutableArray<string>.Empty);
}

/// <summary>Access permitted on a memory region.</summary>
[Flags]
public enum MemoryAccess
{
    None = 0,
    Readable = 1 << 0,
    Writable = 1 << 1,
    Erasable = 1 << 2,
}

/// <summary>A flashable memory region (e.g. internal flash, option bytes, OTP).</summary>
public readonly record struct MemoryRegion(string Name, uint Start, uint Size, uint SectorSize, MemoryAccess Access);
