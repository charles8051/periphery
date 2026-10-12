// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.DebugProbe.AlgorithmGen;

namespace Periphery.DebugProbe.StLink.Tests;

/// <summary>The manifest parser and the .pdsc reader that fills it, on inline inputs.</summary>
public class PackInputTests
{
    private const string Pdsc = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns:xs="http://www.w3.org/2001/XMLSchema-instance">
          <devices><family Dfamily="STM32G4 Series">
            <device Dname="STM32G431CBTx">
              <algorithm name="CMSIS/Flash/STM32G4xx_SB_OPT.FLM" start="0x1FFF7800" size="0x00000018" RAMstart="0x20000000" RAMsize="0x8000" default="0"/>
              <algorithm name="CMSIS/Flash/STM32G43x-4x_128.FLM" start="0x08000000" size="0x00020000" RAMstart="0x20000000" RAMsize="0x8000" default="1"/>
            </device>
            <device Dname="STM32G431KBTx">
              <algorithm name="CMSIS\Flash\stm32g43x-4x_128.flm" start="0x08000000" size="0x00020000" RAMstart="0x20000000" RAMsize="0x8000" default="1"/>
            </device>
          </family></devices>
        </package>
        """;

    private const string Manifest = """
        {
          "pack": "Open-CMSIS-Pack/STM32G4xx_DFP",
          "version": "2.2.0",
          "tag": "v2.2.0",
          "pdsc": "Keil.STM32G4xx_DFP.pdsc",
          "pdscSha256": "5BEB24AB0827BEA12FA62CB114BC48E86EE5E2E78A695A5C5F2BCBB47A279B36",
          "algorithms": [
            {
              "field": "Stm32G43x4x128",
              "flm": "CMSIS/Flash/STM32G43x-4x_128.FLM",
              "flmSha256": "27CB62458B4F45E23D91C64B901464AB1688ABD8DD4F5971054AED83F3B0970A",
              "start": "0x08000000",
              "size": "0x00020000",
              "ramStart": "0x20000000",
              "ramSize": "0x00008000",
              "notice": "STM32G4xx_Flash.NOTICE.txt"
            }
          ]
        }
        """;

    [Fact]
    public void The_pdsc_entry_is_found_without_regard_to_case_or_slashes_and_repeats_collapse()
    {
        Assert.Equal(
            new PackAlgorithm(0x08000000, 0x20000, 0x20000000, 0x8000),
            PdscReader.Find(Pdsc, "CMSIS/Flash/STM32G43x-4x_128.FLM"));
    }

    [Fact]
    public void An_algorithm_the_pdsc_does_not_list_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => PdscReader.Find(Pdsc, "CMSIS/Flash/STM32G49x-Ax_256.FLM"));
    }

    [Fact]
    public void Entries_for_one_FLM_that_disagree_are_refused()
    {
        string conflicting = Pdsc.Replace("""name="CMSIS\Flash\stm32g43x-4x_128.flm" start="0x08000000" size="0x00020000" RAMstart="0x20000000" RAMsize="0x8000""",
                                          """name="CMSIS\Flash\stm32g43x-4x_128.flm" start="0x08000000" size="0x00020000" RAMstart="0x20000000" RAMsize="0x4000""");

        var ex = Assert.Throws<InvalidDataException>(() => PdscReader.Find(conflicting, "CMSIS/Flash/STM32G43x-4x_128.FLM"));
        Assert.Contains("2 different ranges", ex.Message);
    }

    [Fact]
    public void A_manifest_parses_into_its_pack_and_algorithm_entries()
    {
        var manifest = PackManifest.Parse(Manifest);

        Assert.Equal(("Open-CMSIS-Pack/STM32G4xx_DFP", "2.2.0", "v2.2.0"), (manifest.Pack, manifest.Version, manifest.Tag));
        Assert.Equal("5BEB24AB0827BEA12FA62CB114BC48E86EE5E2E78A695A5C5F2BCBB47A279B36", manifest.PdscSha256);
        var algorithm = Assert.Single(manifest.Algorithms);
        Assert.Equal("Stm32G43x4x128", algorithm.Field);
        Assert.Equal(new PackAlgorithm(0x08000000, 0x20000, 0x20000000, 0x8000), algorithm.Window);
        Assert.Equal("STM32G4xx_Flash.NOTICE.txt", algorithm.Notice);
    }

    [Theory]
    [InlineData("\"ramSize\": \"0x00008000\"", "\"ramSize\": \"32768\"")]   // not hex
    [InlineData("\"notice\": \"STM32G4xx_Flash.NOTICE.txt\"", "\"notice\": \"\"")] // empty
    [InlineData("\"tag\": \"v2.2.0\",", "")]                                  // missing
    public void A_manifest_with_a_bad_or_missing_value_is_refused(string from, string to)
    {
        Assert.Throws<InvalidDataException>(() => PackManifest.Parse(Manifest.Replace(from, to)));
    }

    [Fact]
    public void Hex_is_formatted_the_way_the_manifest_stores_it()
    {
        Assert.Equal("0x00008000", PackManifest.FormatHex(0x8000));
    }
}
