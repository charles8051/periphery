// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Time.Testing;
using Periphery.DebugProbe.CortexM;
using Periphery.DebugProbe.Flash;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

/// <summary>
/// The programmer end to end, through the ST-Link session and the fake probe, against the
/// simulated STM32G431. Only the flash algorithm can change the simulated flash.
/// </summary>
public class StLinkProgrammerTests
{
    private static readonly DeviceInfo Probe = new()
    {
        Id = @"USB\VID_0483&PID_374E&MI_00\TEST",
        Name = "ST-Link Debug",
        Category = DeviceCategory.Usb,
    };

    private static async Task<(StLinkProgrammer Programmer, SimulatedStm32G431 Target)> BenchAsync(
        Func<FakeTimeProvider, SimulatedStm32G431>? target = null)
    {
        var time = new FakeTimeProvider();
        var simulated = target?.Invoke(time) ?? new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, time);
        var session = await StLinkSession.OpenAsync(new FakeStLinkProbe(simulated), CancellationToken.None);
        return (StLinkProgrammer.CreateForTest(Probe, session, time), simulated);
    }

    private static FirmwarePayload Hex(params (uint Address, byte[] Data)[] segments) =>
        FirmwarePayload.FromImage(
            FirmwareImage.FromSegments(segments.Select(s => new FirmwareSegment(s.Address, s.Data))),
            FirmwareFormat.IntelHex);

    private static byte[] Original()
    {
        var flash = Enumerable.Repeat((byte)0xFF, 128 * 1024).ToArray();
        flash.AsSpan(0, 0x1000).Fill(0x33); // an application in sectors 0 and 1
        return flash;
    }

    [Fact]
    public async Task Identify_reports_the_part_its_flash_and_the_probe_firmware()
    {
        var (programmer, _) = await BenchAsync();

        var identity = await programmer.IdentifyAsync();

        Assert.Equal("STM32G4", identity.Family);
        Assert.Equal("STM32G431/G441", identity.Chip);
        Assert.Equal("V3J9", identity.BootloaderVersion);
        Assert.Equal(0x400, identity.TransferSize);
        var region = Assert.Single(identity.Regions);
        Assert.Equal((0x08000000u, 0x20000u, 0x800u), (region.Start, region.Size, region.SectorSize));
    }

    [Fact]
    public async Task A_flash_merges_erases_programs_verifies_and_runs()
    {
        var (programmer, target) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t, Original()));
        byte[] vectors = [0x00, 0x40, 0x00, 0x20, 0x09, 0x00, 0x00, 0x08];
        var pattern = Enumerable.Range(0, 0x1000).Select(i => (byte)i).ToArray();

        var result = await programmer.FlashAsync(Hex((0x08000000, vectors), (0x0801F000, pattern)), new FlashOptions());

        Assert.True(result.Success, result.Error);
        Assert.True(result.Verified);
        Assert.Equal(vectors, target.Flash[..8]);
        Assert.All(target.Flash[8..0x1000], b => Assert.Equal(0x33, b)); // rest of sector 0, and sector 1, kept
        Assert.Equal(pattern, target.Flash[0x1F000..]);
        Assert.Equal(
            ["Init(1)", "EraseSector(0x08000000)", "EraseSector(0x0801F000)", "EraseSector(0x0801F800)", "UnInit(1)", "Init(2)"],
            target.Calls.Take(6));
        Assert.Equal(6, target.Calls.Count(c => c.StartsWith("ProgramPage", StringComparison.Ordinal)));
        Assert.Equal(Stm32Registers.WatchdogStopBits, target.WatchdogFreeze & Stm32Registers.WatchdogStopBits);
        Assert.True(target.RunningApplication);
        Assert.Equal(0, target.FlashWritesThroughSeam);
    }

    [Fact]
    public async Task Erase_and_write_progress_are_both_in_bytes()
    {
        var (programmer, _) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t, Original()));
        var reports = new List<FlashProgress>();

        var result = await programmer.FlashAsync(
            Hex((0x08000000, [1, 2, 3, 4]), (0x0801F000, new byte[0x1000])), new FlashOptions(), new ListProgress(reports));

        Assert.True(result.Success, result.Error);
        var erase = reports.Where(r => r.Phase == FlashPhase.Erasing).ToList();
        Assert.Equal([0x800L, 0x1000L, 0x1800L], erase.Select(r => r.BytesDone));
        Assert.All(erase, r => Assert.Equal(0x1800L, r.BytesTotal));
        var write = reports.Last(r => r.Phase == FlashPhase.Writing);
        Assert.Equal(write.BytesTotal, write.BytesDone);
    }

    [Fact]
    public async Task Flashing_what_is_already_there_runs_no_algorithm_call()
    {
        var (programmer, target) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t, Original()));

        var result = await programmer.FlashAsync(Hex((0x08000000, [0x33, 0x33, 0x33, 0x33])), new FlashOptions());

        Assert.True(result.Success, result.Error);
        Assert.Empty(target.Calls);
    }

    [Fact]
    public async Task A_part_off_the_allowlist_is_refused_before_the_core_is_touched()
    {
        var (programmer, target) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t) { Idcode = 0x20016469 });

        var result = await programmer.FlashAsync(Hex((0x08000000, [1, 2, 3, 4])), new FlashOptions());

        Assert.False(result.Success);
        Assert.Contains("0x469", result.Error);
        Assert.False(target.Halted);
        Assert.False(target.Reset);
    }

    [Theory]
    [InlineData(0xFBEFF800u)] // RDP level 1
    [InlineData(0xFBEFF8CCu)] // RDP level 2
    public async Task A_read_protected_part_is_refused_before_the_core_is_touched(uint optr)
    {
        var (programmer, target) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t) { Optr = optr });

        var result = await programmer.FlashAsync(Hex((0x08000000, [1, 2, 3, 4])), new FlashOptions());

        Assert.False(result.Success);
        Assert.Contains("read protection", result.Error);
        Assert.False(target.Halted);
        Assert.False(target.Reset);
    }

    [Fact]
    public async Task An_image_outside_flash_is_refused_before_the_core_is_touched()
    {
        var (programmer, target) = await BenchAsync();

        var result = await programmer.FlashAsync(Hex((0x1FFF7800, [1, 2, 3, 4])), new FlashOptions());

        Assert.False(result.Success);
        Assert.Contains("outside flash", result.Error);
        Assert.False(target.Halted);
    }

    [Fact]
    public async Task A_failing_algorithm_call_fails_the_flash_and_leaves_the_core_halted()
    {
        var (programmer, target) = await BenchAsync();
        target.FailNext = ("ProgramPage", 1);

        var result = await programmer.FlashAsync(Hex((0x08000000, [1, 2, 3, 4])), new FlashOptions());

        Assert.False(result.Success);
        Assert.Contains("ProgramPage(0x08000000) returned 1", result.Error);
        Assert.True(target.Halted);
        Assert.False(target.RunningApplication);
    }

    [Fact]
    public async Task An_algorithm_that_does_not_read_back_is_never_called()
    {
        var (programmer, target) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t) { RamDropsWrites = true });

        var result = await programmer.FlashAsync(Hex((0x08000000, [1, 2, 3, 4])), new FlashOptions());

        Assert.False(result.Success);
        Assert.Contains("did not load", result.Error);
        Assert.Empty(target.Calls);
    }

    [Fact]
    public async Task A_window_that_starts_in_RAM_but_runs_past_it_fails_at_load()
    {
        // The simulated part's RAM ends at 0x20008000. This window starts 1 KB before that and
        // overstates its size, so the code fits but the stack and page buffer do not.
        var time = new FakeTimeProvider();
        var target = new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, time);
        var session = await StLinkSession.OpenAsync(new FakeStLinkProbe(target), CancellationToken.None);
        var overstated = FlashAlgorithms.Stm32G43x4x128 with { RamStart = 0x20007C00, RamSize = 0x8000 };
        var layout = RamLayout.For(overstated);
        Assert.True(layout.CodeAddress + (uint)overstated.Code.Length < 0x20008000);
        Assert.True(layout.End > 0x20008000);

        var runner = new FlashAlgorithmRunner(session, new CortexMDebug(session, time));
        var ex = await Assert.ThrowsAsync<StLinkException>(() => runner.LoadAsync(overstated, layout, CancellationToken.None));

        Assert.Equal(StLinkStatus.SwdApFault, ex.Status);
        Assert.Empty(target.Calls);
    }

    [Fact]
    public async Task A_call_that_never_returns_times_out_on_the_injected_clock()
    {
        var (programmer, _) = await BenchAsync(t => new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, t) { BusyPolls = 100_000 });

        var result = await programmer.FlashAsync(Hex((0x08000000, [1, 2, 3, 4])), new FlashOptions());

        Assert.False(result.Success);
        Assert.Contains("did not finish Init", result.Error);
    }

    private sealed class ListProgress(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => reports.Add(value);
    }

    [Fact]
    public async Task A_kind_2_blob_is_refused()
    {
        var (programmer, _) = await BenchAsync();

        var result = await programmer.FlashAsync(FirmwarePayload.FromBlob(new byte[16], FirmwareFormat.Efm8BootRecords), new FlashOptions());

        Assert.False(result.Success);
    }
}
