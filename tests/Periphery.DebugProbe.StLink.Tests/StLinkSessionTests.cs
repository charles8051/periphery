// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Time.Testing;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

public class StLinkSessionTests
{
    private static (FakeStLinkProbe Probe, SimulatedStm32G431 Target) Bench()
    {
        var target = new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, new FakeTimeProvider());
        return (new FakeStLinkProbe(target), target);
    }

    [Fact]
    public async Task Opening_leaves_mass_storage_mode_enters_SWD_and_opens_access_port_0()
    {
        var (probe, _) = Bench();

        var session = await StLinkSession.OpenAsync(probe, CancellationToken.None);

        Assert.Equal(new StLinkVersion(3, 9), session.Version);
        Assert.Equal(3.30, session.TargetVoltage, 2);
        Assert.Equal(
            ["F5", "F1", "FB", "F7", "F230", "F24B"],
            probe.Commands.Select(c => c[0] == 0xF2 ? $"F2{c[1]:X2}" : $"{c[0]:X2}"));
    }

    [Fact]
    public async Task A_probe_left_in_debug_mode_is_returned_to_idle_first()
    {
        var (probe, _) = Bench();
        probe.Mode = (byte)StLinkMode.Debug;

        await StLinkSession.OpenAsync(probe, CancellationToken.None);

        Assert.Equal([0xF2, 0x21], probe.Commands[1][..2]);
    }

    [Fact]
    public async Task Firmware_below_the_floor_is_refused_before_SWD_is_entered()
    {
        var (probe, _) = Bench();
        probe.ExtendedJtagVersion = 2;

        var ex = await Assert.ThrowsAsync<StLinkException>(() => StLinkSession.OpenAsync(probe, CancellationToken.None));

        Assert.Contains("V3J3", ex.Message);
        Assert.DoesNotContain(probe.Commands, c => c[0] == 0xF2 && c[1] == 0x30);
    }

    [Fact]
    public async Task A_V2_too_old_for_init_AP_skips_it()
    {
        var (probe, _) = Bench();
        probe.VersionWord = (2 << 12) | (27 << 6);

        await StLinkSession.OpenAsync(probe, CancellationToken.None);

        Assert.DoesNotContain(probe.Commands, c => c[0] == 0xFB);
        Assert.DoesNotContain(probe.Commands, c => c[0] == 0xF2 && c[1] == 0x4B);
    }

    [Fact]
    public async Task Word_access_goes_through_the_debug_register_commands()
    {
        var (probe, _) = Bench();
        var session = await StLinkSession.OpenAsync(probe, CancellationToken.None);

        Assert.Equal(0x20036468u, await session.ReadWordAsync(Stm32Registers.DbgmcuIdcode, CancellationToken.None));
    }

    [Fact]
    public async Task A_read_larger_than_one_command_is_split_and_checked_after_each_part()
    {
        var (probe, target) = Bench();
        target.Flash.AsSpan(0, 8192).Fill(0x5A);
        var session = await StLinkSession.OpenAsync(probe, CancellationToken.None);
        probe.Commands.Clear();

        var data = new byte[8192];
        await session.ReadAsync(0x08000000, data, CancellationToken.None);

        Assert.All(data, b => Assert.Equal(0x5A, b));
        Assert.Equal(
            ["F207:6144", "F23E", "F207:2048", "F23E"],
            probe.Commands.Select(c => c[1] == 0x07 ? $"F207:{BitConverter.ToUInt16(c, 6)}" : $"F2{c[1]:X2}"));
    }

    [Fact]
    public async Task A_wait_status_is_retried_and_a_fault_is_not()
    {
        var (probe, target) = Bench();
        var session = await StLinkSession.OpenAsync(probe, CancellationToken.None);

        probe.MemoryStatuses.Enqueue((byte)StLinkStatus.SwdApWait);
        var data = new byte[4];
        await session.ReadAsync(0x20000000, data, CancellationToken.None);

        probe.MemoryStatuses.Enqueue((byte)StLinkStatus.SwdApFault);
        var ex = await Assert.ThrowsAsync<StLinkException>(() => session.ReadAsync(0x20000000, data, CancellationToken.None));
        Assert.Equal(StLinkStatus.SwdApFault, ex.Status);
    }

    [Fact]
    public async Task A_command_error_status_is_reported_with_what_was_asked()
    {
        var (probe, _) = Bench();
        var session = await StLinkSession.OpenAsync(probe, CancellationToken.None);
        probe.CommandStatuses.Enqueue((byte)StLinkStatus.SwdDpFault);

        var ex = await Assert.ThrowsAsync<StLinkException>(
            () => session.WriteWordAsync(0xE000EDF0, 0xA05F0003, CancellationToken.None));

        Assert.Contains("write 0xE000EDF0", ex.Message);
        Assert.Equal(StLinkStatus.SwdDpFault, ex.Status);
    }
}
