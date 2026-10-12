// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.DebugProbe.CortexM;
using Periphery.DebugProbe.Flash;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

public class FlashPlanTests
{
    private static readonly FlashAlgorithm G431 = FlashAlgorithms.Stm32G43x4x128;
    private const uint Flash = 0x08000000;

    private static FirmwareImage Image(params (uint Address, byte[] Data)[] segments) =>
        FirmwareImage.FromSegments(segments.Select(s => new FirmwareSegment(s.Address, s.Data)));

    private static SectorContents Sector(uint address, byte fill, uint size = 0x800) =>
        new(address, Enumerable.Repeat(fill, (int)size).ToArray());

    [Fact]
    public void Sectors_are_found_by_address_from_the_FlashDevice_runs()
    {
        Assert.Equal((0x08000000u, 0x800u), G431.SectorContaining(0x08000000));
        Assert.Equal((0x08000800u, 0x800u), G431.SectorContaining(0x08000FFF));
        Assert.Equal((0x0801F800u, 0x800u), G431.SectorContaining(0x0801FFFF));
    }

    [Fact]
    public void An_image_touches_every_sector_it_overlaps_once_in_order()
    {
        var image = Image((0x08000F00, new byte[0x200]), (0x08000010, new byte[4]));

        var sectors = FlashPlan.SectorsToRead(image, G431, 0x20000);

        Assert.Equal([(0x08000000u, 0x800u), (0x08000800u, 0x800u), (0x08001000u, 0x800u)], sectors);
    }

    [Theory]
    [InlineData(0x07FFFFFC, 8)]    // starts below flash
    [InlineData(0x0801FFFC, 8)]    // runs past the algorithm's 128 KB
    [InlineData(0x1FFF7800, 4)]    // option bytes
    public void A_segment_outside_flash_is_refused(uint address, int length)
    {
        var ex = Assert.Throws<FlashPlanException>(() => FlashPlan.SectorsToRead(Image((address, new byte[length])), G431, 0x20000));
        Assert.Contains("outside flash", ex.Message);
    }

    [Fact]
    public void The_range_is_cut_to_the_flash_size_the_part_reports()
    {
        // A 64 KB part under the 128 KB algorithm: 0x08010000 is not flash on it.
        var image = Image((0x08010000, new byte[4]));

        Assert.Throws<FlashPlanException>(() => FlashPlan.SectorsToRead(image, G431, 0x10000));
        Assert.Single(FlashPlan.SectorsToRead(image, G431, 0x20000));
    }

    [Fact]
    public void An_empty_image_is_refused()
    {
        Assert.Throws<FlashPlanException>(() => FlashPlan.SectorsToRead(FirmwareImage.Empty, G431, 0x20000));
    }

    [Fact]
    public void Merging_keeps_every_byte_the_image_does_not_cover()
    {
        var current = Sector(Flash, 0x11);
        var image = Image((Flash + 0x10, [0xAA, 0xBB, 0xCC, 0xDD]));

        var merged = Assert.Single(FlashPlan.Merge([current], image));

        Assert.True(merged.Changed);
        var bytes = merged.Data.ToArray();
        Assert.Equal([0xAA, 0xBB, 0xCC, 0xDD], bytes[0x10..0x14]);
        Assert.All(bytes[..0x10], b => Assert.Equal(0x11, b));
        Assert.All(bytes[0x14..], b => Assert.Equal(0x11, b));
    }

    [Fact]
    public void A_sector_that_already_holds_the_image_is_unchanged()
    {
        var current = Sector(Flash, 0xAA);
        var merged = Assert.Single(FlashPlan.Merge([current], Image((Flash, [0xAA, 0xAA, 0xAA, 0xAA]))));

        Assert.False(merged.Changed);
        Assert.Empty(FlashPlan.Steps([merged], G431, RamLayout.For(G431)));
    }

    [Fact]
    public void Steps_erase_changed_sectors_then_program_only_pages_that_are_not_blank()
    {
        var layout = RamLayout.For(G431);
        var programmed = new byte[0x800];
        programmed.AsSpan(0x400).Fill(0xFF); // second page left erased
        MergedSector[] merged =
        [
            new(Flash, programmed, Changed: true),
            new(Flash + 0x800, new byte[0x800], Changed: false),
        ];

        var steps = FlashPlan.Steps(merged, G431, layout);

        Assert.Equal(
            ["Init(1)", "EraseSector(0x08000000)", "UnInit(1)", "Init(2)", "buffer", "ProgramPage(0x08000000)", "UnInit(2)"],
            steps.Select(Describe));
        var program = steps.OfType<FlashStep.Call>().Single(s => s.Algorithm.Name == "ProgramPage").Algorithm;
        Assert.Equal((0x08000000u, (uint?)0x400u, (uint?)layout.BufferAddress), (program.R0, program.R1, program.R2));
    }

    [Fact]
    public void The_RAM_layout_puts_header_code_stack_and_buffer_in_order_without_overlap()
    {
        var layout = RamLayout.For(G431);

        Assert.Equal(0x20000000u, layout.HeaderAddress);
        Assert.Equal(0x20000004u, layout.CodeAddress);
        Assert.Equal(layout.CodeAddress + G431.DataSectionOffset, layout.StaticBase);
        Assert.True(layout.StackTop - RamLayout.DefaultStackSize >= layout.CodeAddress + (uint)G431.Code.Length);
        Assert.Equal(0u, layout.StackTop % 8);
        Assert.Equal(layout.StackTop, layout.BufferAddress);
        Assert.Equal(layout.BufferAddress + G431.PageSize, layout.End);
        Assert.True(layout.End <= G431.RamStart + G431.RamSize);
    }

    [Fact]
    public void An_algorithm_too_large_for_its_window_is_refused()
    {
        var cramped = G431 with { RamSize = 0x400 };

        var ex = Assert.Throws<FlashPlanException>(() => RamLayout.For(cramped));
        Assert.Contains("needs", ex.Message);
    }

    [Fact]
    public void An_init_call_sets_static_base_and_stack_and_every_call_returns_to_the_header()
    {
        var layout = RamLayout.For(G431);

        var init = AlgorithmCall.Init(G431, FlashOperation.Program).RegisterWrites(layout).ToDictionary(w => w.Register, w => w.Value);
        var erase = AlgorithmCall.EraseSector(G431, 0x08000800).RegisterWrites(layout).ToDictionary(w => w.Register, w => w.Value);

        Assert.Equal(G431.FlashStart, init[CoreRegister.R0]);
        Assert.Equal(0u, init[CoreRegister.R1]);
        Assert.Equal(2u, init[CoreRegister.R2]);
        Assert.Equal(layout.StaticBase, init[CoreRegister.R9]);
        Assert.Equal(layout.StackTop, init[CoreRegister.Sp]);
        Assert.False(erase.ContainsKey(CoreRegister.R9));
        Assert.False(erase.ContainsKey(CoreRegister.Sp));
        Assert.All(new[] { init, erase }, w =>
        {
            Assert.Equal(layout.HeaderAddress | 1, w[CoreRegister.Lr]);
            Assert.Equal(CortexMRegisters.XpsrThumb, w[CoreRegister.Xpsr]);
        });
        Assert.Equal(layout.CodeAddress + G431.EraseSectorOffset, erase[CoreRegister.DebugReturnAddress]);
    }

    [Fact]
    public void The_PC_is_written_last()
    {
        var writes = AlgorithmCall.Init(G431, FlashOperation.Erase).RegisterWrites(RamLayout.For(G431));
        Assert.Equal(CoreRegister.DebugReturnAddress, writes[^1].Register);
    }

    private static string Describe(FlashStep step) => step switch
    {
        FlashStep.WriteBuffer => "buffer",
        FlashStep.Call { Algorithm: { Name: "Init" or "UnInit" } call } => $"{call.Name}({(call.Name == "Init" ? call.R2 : call.R0)})",
        FlashStep.Call { Algorithm: var call } => $"{call.Name}(0x{call.R0:X8})",
        _ => "?",
    };
}
