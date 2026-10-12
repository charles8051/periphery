// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.DebugProbe.StLink.Tests;

public class StLinkProtocolTests
{
    [Fact]
    public void Every_command_is_a_sixteen_byte_block()
    {
        byte[][] commands =
        [
            StLinkProtocol.GetVersion(), StLinkProtocol.GetVersionExtended(), StLinkProtocol.GetCurrentMode(),
            StLinkProtocol.GetTargetVoltage(), StLinkProtocol.EnterSwd(), StLinkProtocol.InitAp(0),
            StLinkProtocol.ReadMem32(0x08000000, 4), StLinkProtocol.WriteMem32(0x20000000, 4),
            StLinkProtocol.GetLastRwStatus2(), StLinkProtocol.ReadDebugReg(0xE000EDF0),
            StLinkProtocol.WriteDebugReg(0xE000EDF0, 1),
        ];
        Assert.All(commands, c => Assert.Equal(16, c.Length));
    }

    [Fact]
    public void A_32_bit_read_carries_address_length_and_access_port()
    {
        Assert.Equal(
            Convert.FromHexString("F207F0EF00E000180000000000000000"),
            StLinkProtocol.ReadMem32(0xE000EFF0, 0x1800));
    }

    [Fact]
    public void A_debug_register_write_carries_address_then_value()
    {
        Assert.Equal(
            Convert.FromHexString("F235F0ED00E003005FA0000000000000"),
            StLinkProtocol.WriteDebugReg(0xE000EDF0, 0xA05F0003));
    }

    [Fact]
    public void Entering_SWD_is_the_enter2_command_with_the_SWD_selector()
    {
        Assert.Equal(Convert.FromHexString("F230A300000000000000000000000000"), StLinkProtocol.EnterSwd());
    }

    [Theory]
    [InlineData(0x08000002, 4)]   // unaligned address
    [InlineData(0x08000000, 6)]   // not whole words
    [InlineData(0x08000000, 0)]   // empty
    [InlineData(0x08000000, 6148)] // past the read limit
    public void A_32_bit_read_refuses_a_transfer_the_probe_cannot_make(uint address, int length)
    {
        Assert.ThrowsAny<ArgumentException>(() => StLinkProtocol.ReadMem32(address, length));
    }

    [Fact]
    public void Each_exit_command_leaves_its_own_mode()
    {
        Assert.Equal([0xF3, 0x07], StLinkProtocol.ExitMode(StLinkMode.Dfu)![..2]);
        Assert.Equal([0xF2, 0x21], StLinkProtocol.ExitMode(StLinkMode.Debug)![..2]);
        Assert.Equal([0xF4, 0x01], StLinkProtocol.ExitMode(StLinkMode.Swim)![..2]);
        Assert.Null(StLinkProtocol.ExitMode(StLinkMode.MassStorage));
    }

    [Fact]
    public void A_V2_reports_its_firmware_in_the_version_word()
    {
        // V2J37: hardware 2 in bits 15:12, JTAG 37 in bits 11:6.
        ushort word = (2 << 12) | (37 << 6);
        var version = StLinkProtocol.DecodeVersion([(byte)(word >> 8), (byte)word, 0x83, 0x04, 0x48, 0x37]);
        Assert.Equal(new StLinkVersion(2, 37), version);
        Assert.Equal("V2J37", version.ToString());
    }

    [Fact]
    public void A_V3_reports_its_firmware_in_the_extended_reply()
    {
        var version = StLinkProtocol.DecodeVersion(
            [0x30, 0x00, 0x83, 0x04, 0x4E, 0x37],
            [3, 0, 9, 0, 0, 0, 0, 0, 0x83, 0x04, 0x4E, 0x37]);
        Assert.Equal(new StLinkVersion(3, 9), version);
    }

    [Theory]
    [InlineData(2, 25, false)]
    [InlineData(2, 26, true)]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 0, false)]
    public void The_firmware_floor_follows_ADR_0094_D7(int hardware, int jtag, bool accepted)
    {
        Assert.Equal(accepted, StLinkProtocol.FirmwareFloorViolation(new StLinkVersion(hardware, jtag)) is null);
    }

    [Fact]
    public void Target_voltage_is_twice_a1_times_1_2_over_a0()
    {
        Assert.Equal(3.30, StLinkProtocol.DecodeTargetVoltage([0xE8, 0x03, 0, 0, 0x5F, 0x05, 0, 0]), 2);
    }

    [Fact]
    public void Wait_statuses_are_the_retryable_ones()
    {
        Assert.True(StLinkProtocol.IsWait(StLinkStatus.SwdApWait));
        Assert.True(StLinkProtocol.IsWait(StLinkStatus.SwdDpWait));
        Assert.False(StLinkProtocol.IsWait(StLinkStatus.SwdApFault));
        Assert.False(StLinkProtocol.IsWait(StLinkStatus.Ok));
    }

    [Fact]
    public void Every_listed_model_uses_the_probe_rs_endpoints()
    {
        Assert.Equal(0x02, StLinkProtocol.Models[0x3748].OutEndpoint);
        Assert.All(StLinkProtocol.Models.Values.Where(m => m.ProductId != 0x3748), m => Assert.Equal(0x01, m.OutEndpoint));
        Assert.All(StLinkProtocol.Models.Values, m => Assert.Equal(0x81, m.InEndpoint));
    }
}
