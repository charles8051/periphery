// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Security.Cryptography;
using Periphery.DebugProbe.AlgorithmGen;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

/// <summary>
/// ADR-0094's generator gate: the checked-in record is exactly what the generator makes from the
/// checked-in FLM, and the FLM is the file the record names.
/// </summary>
public class AlgorithmGeneratorTests
{
    private const string PinnedSha256 = "27CB62458B4F45E23D91C64B901464AB1688ABD8DD4F5971054AED83F3B0970A";

    private static readonly string PackDir = Path.Combine(RepoRoot(), "src", "Periphery.DebugProbe.AlgorithmGen", "Packs", "STM32G4xx_DFP-2.2.0");
    private static readonly string RecordFile = Path.Combine(RepoRoot(), "src", "Periphery.DebugProbe.StLink", "Targets", "FlashAlgorithms.Stm32G43x4x128.g.cs");

    private static RecordInput Load() => Generator.Load(
        PackDir, "Open-CMSIS-Pack/STM32G4xx_DFP", "2.2.0", "CMSIS/Flash/STM32G43x-4x_128.FLM",
        "Keil.STM32G4xx_DFP.pdsc", "STM32G4xx_Flash.NOTICE.txt", "Stm32G43x4x128");

    [Fact]
    public void The_checked_in_record_is_what_the_generator_writes()
    {
        string expected = RecordWriter.Write(Load());
        string actual = File.ReadAllText(RecordFile).Replace("\r\n", "\n");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void The_FLM_is_the_file_the_record_names()
    {
        var flm = File.ReadAllBytes(Path.Combine(PackDir, "STM32G43x-4x_128.FLM"));

        Assert.Equal(PinnedSha256, Convert.ToHexString(SHA256.HashData(flm)));
        Assert.Equal(PinnedSha256, FlashAlgorithms.Stm32G43x4x128.Provenance.Sha256);
    }

    [Fact]
    public void The_FLM_reads_as_the_main_flash_algorithm_its_source_describes()
    {
        var flm = Load().Flm;

        // FlashDev.c, STM32G43x_128: on-chip, 128 KB at 0x08000000, 1 KB pages, 2 KB sectors, 400 ms.
        Assert.Equal("STM32G43x-4x 128 KB Flash", flm.Name);
        Assert.Equal(1, flm.DeviceType);
        Assert.Equal((0x08000000u, 0x20000u, 0x400u), (flm.FlashStart, flm.FlashSize, flm.PageSize));
        Assert.Equal([(0x800u, 0u)], flm.Sectors);
        Assert.Equal((400u, 400u), (flm.ProgramPageTimeoutMs, flm.EraseSectorTimeoutMs));
        Assert.Equal(0xFF, flm.ErasedValue);
    }

    [Fact]
    public void Entry_points_match_probe_rs_for_the_same_source()
    {
        // probe-rs's STM32G47x-8x_512 entry, built from the same FlashPrg.c, lists pc_uninit 0x5b,
        // pc_erase_sector 0x91, pc_program_page 0x10f and data_section_offset 0x1a0, with the
        // Thumb bit set on the entry points.
        var flm = Load().Flm;

        Assert.Equal((0x0u, 0x5Au, 0x90u, 0x10Eu, 0x1A0u),
            (flm.InitOffset, flm.UnInitOffset, flm.EraseSectorOffset, flm.ProgramPageOffset, flm.DataSectionOffset));
    }

    [Fact]
    public void The_pack_gives_the_algorithm_a_32_KB_window_at_the_start_of_SRAM()
    {
        Assert.Equal(new PdscAlgorithm(0x08000000, 0x20000, 0x20000000, 0x8000), Load().Pdsc);
    }

    [Fact]
    public void An_option_byte_algorithm_is_not_a_main_flash_record()
    {
        // STM32G4xx_SB_OPT.FLM covers 0x1FFF7800. D9: such algorithms are never generated, and the
        // generator's pack check would refuse to pair one with a main-flash range.
        var input = Load();
        var optionBytes = input with { Pdsc = input.Pdsc with { Start = 0x1FFF7800, Size = 0x18 } };

        Assert.Throws<InvalidDataException>(() => RecordWriter.Write(optionBytes));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Periphery.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Periphery.slnx not found above the test output.");
    }
}
