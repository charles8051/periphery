// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Security.Cryptography;
using Periphery.DebugProbe.AlgorithmGen;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

/// <summary>
/// ADR-0094's generator gate: the checked-in record is exactly what the generator makes from the
/// checked-in FLM and manifest, and the FLM is the file both name.
/// </summary>
public class AlgorithmGeneratorTests
{
    private const string PinnedSha256 = "27CB62458B4F45E23D91C64B901464AB1688ABD8DD4F5971054AED83F3B0970A";

    private static readonly string PackDir = Path.Combine(RepoRoot(), "src", "Periphery.DebugProbe.AlgorithmGen", "Packs", "STM32G4xx_DFP-2.2.0");
    private static readonly string Manifest = Path.Combine(PackDir, "manifest.json");
    private static readonly string RecordFile = Path.Combine(RepoRoot(), "src", "Periphery.DebugProbe.StLink", "Targets", "FlashAlgorithms.Stm32G43x4x128.g.cs");

    private static RecordInput Load() => Assert.Single(Generator.LoadAll(Manifest));

    [Fact]
    public void The_checked_in_record_is_what_the_generator_writes()
    {
        string expected = RecordWriter.Write(Load());
        string actual = File.ReadAllText(RecordFile).Replace("\r\n", "\n");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void The_FLM_is_the_file_the_manifest_and_the_record_name()
    {
        var flm = File.ReadAllBytes(Path.Combine(PackDir, "STM32G43x-4x_128.FLM"));

        Assert.Equal(PinnedSha256, Convert.ToHexString(SHA256.HashData(flm)));
        Assert.Equal(PinnedSha256, PackManifest.Parse(File.ReadAllText(Manifest)).Algorithms.Single().FlmSha256);
        Assert.Equal(PinnedSha256, FlashAlgorithms.Stm32G43x4x128.Provenance.Sha256);
    }

    [Fact]
    public void A_manifest_pinning_another_FLM_is_refused()
    {
        string dir = Directory.CreateTempSubdirectory("flash-algorithm-gen-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(PackDir))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            string manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(PinnedSha256, new string('0', 64)));

            var ex = Assert.Throws<InvalidDataException>(() => Generator.LoadAll(manifest));
            Assert.Contains("the manifest pins", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
    public void The_manifest_gives_the_algorithm_a_32_KB_window_at_the_start_of_SRAM()
    {
        // The values flash-algorithm-gen extract read from the v2.2.0 .pdsc, SHA-256 5BEB24AB…
        Assert.Equal(new PackAlgorithm(0x08000000, 0x20000, 0x20000000, 0x8000), Load().Window);
    }

    [Theory]
    [InlineData(0x2000_0000u, 0u)]           // empty
    [InlineData(0x2000_0002u, 0x8000u)]      // not word-aligned
    [InlineData(0x1000_0000u, 0x8000u)]      // below the SRAM region
    [InlineData(0x3FFF_C000u, 0x8000u)]      // runs past it
    [InlineData(0x0800_0000u, 0x8000u)]      // the flash it programs
    public void A_RAM_window_that_cannot_be_target_RAM_is_refused(uint ramStart, uint ramSize)
    {
        var input = Load();
        var bad = input with { Window = input.Window with { RamStart = ramStart, RamSize = ramSize } };

        var ex = Assert.Throws<InvalidDataException>(() => RecordWriter.Write(bad));
        Assert.Contains("RAM window", ex.Message);
    }

    [Fact]
    public void A_pdsc_that_matches_the_manifest_passes_the_check()
    {
        string dir = CopyPack();
        try
        {
            string manifest = Path.Combine(dir, "manifest.json");
            string pdsc = Path.Combine(dir, "pack.pdsc");
            File.WriteAllText(pdsc, PdscListing("0x8000"));
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(PinnedPdscSha256, Sha256Of(pdsc)));

            Assert.Single(Generator.LoadAll(manifest, pdsc));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_pdsc_that_lists_another_window_is_refused()
    {
        string dir = CopyPack();
        try
        {
            string manifest = Path.Combine(dir, "manifest.json");
            string pdsc = Path.Combine(dir, "pack.pdsc");
            File.WriteAllText(pdsc, PdscListing("0x4000"));
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(PinnedPdscSha256, Sha256Of(pdsc)));

            var ex = Assert.Throws<InvalidDataException>(() => Generator.LoadAll(manifest, pdsc));
            Assert.Contains("the pack lists", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_pdsc_that_is_not_the_pinned_one_is_refused()
    {
        string dir = CopyPack();
        try
        {
            string pdsc = Path.Combine(dir, "pack.pdsc");
            File.WriteAllText(pdsc, PdscListing("0x8000"));

            var ex = Assert.Throws<InvalidDataException>(() => Generator.LoadAll(Path.Combine(dir, "manifest.json"), pdsc));
            Assert.Contains("the manifest pins", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string PinnedPdscSha256 = "5BEB24AB0827BEA12FA62CB114BC48E86EE5E2E78A695A5C5F2BCBB47A279B36";

    private static string PdscListing(string ramSize) =>
        "<package><devices><family><device Dname=\"STM32G431KBTx\">" +
        "<algorithm name=\"CMSIS/Flash/STM32G43x-4x_128.FLM\" start=\"0x08000000\" size=\"0x00020000\" " +
        $"RAMstart=\"0x20000000\" RAMsize=\"{ramSize}\" default=\"1\"/>" +
        "</device></family></devices></package>";

    private static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string CopyPack()
    {
        string dir = Directory.CreateTempSubdirectory("flash-algorithm-gen-").FullName;
        foreach (var file in Directory.GetFiles(PackDir))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        return dir;
    }

    [Fact]
    public void An_option_byte_range_is_not_a_main_flash_record()
    {
        // STM32G4xx_SB_OPT.FLM covers 0x1FFF7800. D9: such algorithms are never generated, and the
        // writer refuses to pair the main-flash FLM with any other range.
        var input = Load();
        var optionBytes = input with { Window = input.Window with { Start = 0x1FFF7800, Size = 0x18 } };

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
