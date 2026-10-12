// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// The section names and the code, data, BSS layout follow probe-rs's target-gen
// (target-gen/src/algorithm_binary.rs, MIT OR Apache-2.0, Copyright (c) 2019 probe-rs). The
// FlashDevice layout is CMSIS's FlashOS.h. See THIRD-PARTY-NOTICES.md in Periphery.DebugProbe.StLink.

using System.Buffers.Binary;
using System.Text;

namespace Periphery.DebugProbe.AlgorithmGen;

/// <summary>What an FLM file holds, in the shape the runtime record needs.</summary>
internal sealed record FlmAlgorithm(
    string Name,
    ushort DeviceType,
    byte[] Code,
    uint InitOffset,
    uint UnInitOffset,
    uint EraseSectorOffset,
    uint ProgramPageOffset,
    uint DataSectionOffset,
    uint FlashStart,
    uint FlashSize,
    uint PageSize,
    byte ErasedValue,
    uint ProgramPageTimeoutMs,
    uint EraseSectorTimeoutMs,
    IReadOnlyList<(uint Size, uint Offset)> Sectors);

/// <summary>
/// Reads a CMSIS flash algorithm from its FLM file, a 32-bit little-endian ELF image. Code is in
/// section <c>PrgCode</c>, data in <c>PrgData</c> (PROGBITS) with its BSS as a NOBITS <c>PrgData</c>,
/// and the FlashDevice record is the symbol <c>FlashDevice</c>.
/// </summary>
internal static class FlmReader
{
    private const uint ShtProgbits = 1;
    private const uint ShtSymtab = 2;
    private const uint ShtNobits = 8;
    private const uint FlashDeviceSectorsOffset = 160;

    private sealed record Section(string Name, uint Type, uint Address, uint Offset, uint Size, uint Link);

    public static FlmAlgorithm Read(ReadOnlySpan<byte> elf)
    {
        if (elf.Length < 52 || !elf[..4].SequenceEqual("\u007FELF"u8))
            throw new InvalidDataException("Not an ELF file.");
        if (elf[4] != 1 || elf[5] != 1)
            throw new InvalidDataException("Not a 32-bit little-endian ELF file.");

        var sections = ReadSections(elf);
        var code = Find(sections, "PrgCode", ShtProgbits)
            ?? throw new InvalidDataException("No PrgCode section.");
        var data = Find(sections, "PrgData", ShtProgbits)
            ?? new Section("PrgData", ShtProgbits, code.Address + code.Size, 0, 0, 0);
        var bss = Find(sections, "PrgData", ShtNobits)
            ?? new Section("PrgData", ShtNobits, data.Address + data.Size, 0, 0, 0);

        if (data.Address != code.Address + code.Size || bss.Address != data.Address + data.Size)
            throw new InvalidDataException("PrgCode, PrgData and its BSS are not contiguous.");

        var blob = new byte[code.Size + data.Size + bss.Size];
        elf.Slice((int)code.Offset, (int)code.Size).CopyTo(blob);
        if (data.Size > 0)
            elf.Slice((int)data.Offset, (int)data.Size).CopyTo(blob.AsSpan((int)code.Size));

        var symbols = ReadSymbols(elf, sections);
        uint Entry(string name) => symbols.TryGetValue(name, out var value)
            ? (value & ~1u) - code.Address
            : throw new InvalidDataException($"No {name} symbol.");

        if (!symbols.TryGetValue("FlashDevice", out uint deviceAddress))
            throw new InvalidDataException("No FlashDevice symbol.");
        var deviceSection = sections.FirstOrDefault(s => s.Type == ShtProgbits
                && deviceAddress >= s.Address && deviceAddress < s.Address + s.Size)
            ?? throw new InvalidDataException("FlashDevice is in no section with data.");
        var device = elf[(int)(deviceSection.Offset + (deviceAddress - deviceSection.Address))..];

        string name = Encoding.ASCII.GetString(device.Slice(2, 128)).TrimEnd('\0').Split('\0')[0];
        var sectors = new List<(uint, uint)>();
        for (int at = (int)FlashDeviceSectorsOffset; ; at += 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(device[at..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(device[(at + 4)..]);
            if (size == 0xFFFFFFFF && offset == 0xFFFFFFFF)
                break;
            sectors.Add((size, offset));
        }
        if (sectors.Count == 0)
            throw new InvalidDataException("FlashDevice lists no sectors.");

        return new FlmAlgorithm(
            Name: name,
            DeviceType: BinaryPrimitives.ReadUInt16LittleEndian(device[130..]),
            Code: blob,
            InitOffset: Entry("Init"),
            UnInitOffset: Entry("UnInit"),
            EraseSectorOffset: Entry("EraseSector"),
            ProgramPageOffset: Entry("ProgramPage"),
            DataSectionOffset: data.Address - code.Address,
            FlashStart: BinaryPrimitives.ReadUInt32LittleEndian(device[132..]),
            FlashSize: BinaryPrimitives.ReadUInt32LittleEndian(device[136..]),
            PageSize: BinaryPrimitives.ReadUInt32LittleEndian(device[140..]),
            ErasedValue: device[148],
            ProgramPageTimeoutMs: BinaryPrimitives.ReadUInt32LittleEndian(device[152..]),
            EraseSectorTimeoutMs: BinaryPrimitives.ReadUInt32LittleEndian(device[156..]),
            Sectors: sectors);
    }

    private static List<Section> ReadSections(ReadOnlySpan<byte> elf)
    {
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[0x20..]);
        ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(elf[0x2E..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[0x30..]);
        ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(elf[0x32..]);

        var raw = new List<(uint Name, uint Type, uint Address, uint Offset, uint Size, uint Link)>();
        for (int i = 0; i < shnum; i++)
        {
            var sh = elf.Slice((int)(shoff + i * shentsize), shentsize);
            raw.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(sh),
                BinaryPrimitives.ReadUInt32LittleEndian(sh[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(sh[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(sh[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(sh[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(sh[24..])));
        }

        var names = raw[shstrndx];
        var sections = new List<Section>(raw.Count);
        foreach (var s in raw)
            sections.Add(new Section(ReadString(elf, names.Offset + s.Name), s.Type, s.Address, s.Offset, s.Size, s.Link));
        return sections;
    }

    private static Dictionary<string, uint> ReadSymbols(ReadOnlySpan<byte> elf, List<Section> sections)
    {
        var symtab = sections.FirstOrDefault(s => s.Type == ShtSymtab)
            ?? throw new InvalidDataException("No symbol table.");
        var strtab = sections[(int)symtab.Link];

        var symbols = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (uint at = symtab.Offset; at + 16 <= symtab.Offset + symtab.Size; at += 16)
        {
            var sym = elf.Slice((int)at, 16);
            string name = ReadString(elf, strtab.Offset + BinaryPrimitives.ReadUInt32LittleEndian(sym));
            if (name.Length > 0)
                symbols.TryAdd(name, BinaryPrimitives.ReadUInt32LittleEndian(sym[4..]));
        }
        return symbols;
    }

    private static Section? Find(List<Section> sections, string name, uint type) =>
        sections.FirstOrDefault(s => s.Name == name && s.Type == type);

    private static string ReadString(ReadOnlySpan<byte> elf, uint offset)
    {
        var tail = elf[(int)offset..];
        int end = tail.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? tail : tail[..end]);
    }
}
