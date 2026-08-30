// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The EFM8 part's flash region map — selects page size and address bounds, the
/// <c>hex2boot -m</c> option. <see cref="Ub1"/> is the EFM8 Universal Bee 1
/// (e.g. EFM8UB10F16G, the Treehopper part).
/// </summary>
public enum Efm8FlashMap
{
    /// <summary>No special map: one region 0x0000-0xFBFF, 512-byte pages.</summary>
    Default,
    /// <summary>EFM8 Busy Bee 2: 0x0000-0x3DFF (512B) + 0xF800-0xFBBF (64B).</summary>
    Bb2,
    /// <summary>EFM8 Sleepy Bee 2: one region 0x0000-0xF7FF, 1024-byte pages.</summary>
    Sb2,
    /// <summary>EFM8 Universal Bee 1: 0x0000-0x3DFF (512B) + 0xF800-0xFBBF (64B).</summary>
    Ub1,
}

/// <summary>How each flash page is cleared before its data is written (<c>hex2boot -e</c>).</summary>
public enum Efm8EraseMode
{
    /// <summary>No erase records — write only (assumes already-erased flash).</summary>
    None = 0,
    /// <summary>A separate Erase (0x32) record before each page's Write (0x33).</summary>
    Separate = 1,
    /// <summary>One Erase-with-data (0x32) record that erases the page and writes its data. The hex2boot default.</summary>
    WithData = 2,
}

/// <summary>
/// Parameters for converting an Intel HEX image into an EFM8 boot-record stream,
/// mirroring the <c>hex2boot</c> CLI options. A pure value; the same options +
/// image yield the same bytes (ADR-0052).
/// </summary>
/// <param name="Map">Flash region map (<c>-m</c>). Default <see cref="Efm8FlashMap.Default"/>.</param>
/// <param name="Bank">Flash bank (<c>-b</c>, 0 or 1).</param>
/// <param name="Erase">Page-clear strategy (<c>-e</c>). Default <see cref="Efm8EraseMode.WithData"/>.</param>
/// <param name="Start">Start address bound (<c>-s</c>).</param>
/// <param name="Top">Top address bound (<c>-t</c>).</param>
/// <param name="Ids">Identity values for leading Identify (0x30) records (<c>-i</c>); empty = none.</param>
/// <param name="Lock">Lock byte for a trailing Lock (0x35) record (<c>-l</c>); <see langword="null"/> = none. <b>Leave null</b> — a lock can permanently disable bootloader writes / debug access.</param>
/// <param name="Wait">When <see langword="true"/>, omit the trailing RunApp (0x36) and remain in the bootloader (<c>-w</c>).</param>
// A record CLASS, deliberately not a record struct: a record struct's primary-ctor
// default values are skipped by the implicit parameterless / default constructor
// (new()/default zero-initialize every field), which on this brick-capable path would
// silently yield Top=0 and Erase=None. As a class, new() applies the defaults below,
// and there is no zeroed-default footgun.
public sealed record Efm8BootOptions(
    Efm8FlashMap Map = Efm8FlashMap.Default,
    int Bank = 0,
    Efm8EraseMode Erase = Efm8EraseMode.WithData,
    int Start = 0x0000,
    int Top = 0xFFFF,
    ImmutableArray<ushort> Ids = default,
    ushort? Lock = null,
    bool Wait = false)
{
    /// <summary>
    /// The EFM8UB1 (Treehopper EFM8UB10F16G) profile: the exact flags Treehopper's
    /// build uses — <c>-m ub1 -b 0</c>, everything else at the hex2boot default
    /// (erase-with-data, no identity, no lock, emit RunApp).
    /// </summary>
    public static Efm8BootOptions Ub1 { get; } = new() { Map = Efm8FlashMap.Ub1 };

    /// <summary>The identity values, treating <c>default</c> as empty.</summary>
    public ImmutableArray<ushort> IdsOrEmpty => Ids.IsDefault ? ImmutableArray<ushort>.Empty : Ids;
}
