// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// Bench probe for ADR-0094. Opens the first ST-Link debug function Periphery enumerates.
//
//   StLinkProbe                      identify the probe and target (read-only)
//   StLinkProbe dump <file>          save main flash, read twice and kept only when both agree
//   StLinkProbe read <hex addr> <n>  print n bytes of target memory
//   StLinkProbe flash <file>         flash an image (.hex, .bin at 0x08000000, .elf), verify, reset and run

using System.Globalization;
using System.Security.Cryptography;
using Periphery;
using Periphery.Bootloader;
using Periphery.DebugProbe.StLink;
using Periphery.Firmware;

var provider = new StLinkProvider();
var all = await Devices.Enumerate().ToListAsync();
var probe = all.FirstOrDefault(provider.CanHandle);
if (probe is null)
{
    Console.Error.WriteLine("No ST-Link debug function found.");
    return 4;
}

await using var programmer = await StLinkProgrammer.OpenAsync(probe);
var identity = await programmer.IdentifyAsync();
Console.WriteLine($"probe     {probe.Id}, firmware {identity.BootloaderVersion}, target {programmer.TargetVoltage:F2} V");
Console.WriteLine($"target    {identity.Family} {identity.Chip}");
foreach (var region in identity.Regions)
    Console.WriteLine($"region    {region.Name} 0x{region.Start:X8} +0x{region.Size:X}, sector 0x{region.SectorSize:X}");

switch (args)
{
    case ["dump", var path]:
    {
        var flash = identity.Regions.Single();
        var first = await programmer.ReadAsync(flash.Start, (int)flash.Size);
        var second = await programmer.ReadAsync(flash.Start, (int)flash.Size);
        if (!first.AsSpan().SequenceEqual(second))
        {
            Console.Error.WriteLine("dump: two reads of flash disagree; nothing written.");
            return 1;
        }
        await File.WriteAllBytesAsync(path, first);
        Console.WriteLine($"dump      {first.Length} bytes to {path}, SHA-256 {Convert.ToHexString(SHA256.HashData(first))}");
        return 0;
    }

    case ["read", var hexAddress, var count]:
    {
        uint address = uint.Parse(hexAddress.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var data = await programmer.ReadAsync(address, int.Parse(count, CultureInfo.InvariantCulture));
        Console.WriteLine($"0x{address:X8}  {Convert.ToHexString(data)}");
        return 0;
    }

    case ["flash", var path]:
    {
        var payload = FirmwarePayload.Load(await File.ReadAllBytesAsync(path), Path.GetFileName(path), 0x08000000);
        var result = await programmer.FlashAsync(payload, new FlashOptions(), new ConsoleProgress());
        Console.WriteLine($"flash     {result.Describe()}");
        return result.Success ? 0 : 1;
    }

    case []:
        return 0;

    default:
        Console.Error.WriteLine("usage: StLinkProbe [dump <file> | read <hex addr> <n> | flash <file>]");
        return 2;
}

/// <summary>Prints each progress report on the thread that raised it, so lines stay in order.</summary>
sealed class ConsoleProgress : IProgress<FlashProgress>
{
    public void Report(FlashProgress value) =>
        Console.WriteLine($"  {value.Phase,-11} {value.BytesDone,7}/{value.BytesTotal,-7} {value.Message}");
}
