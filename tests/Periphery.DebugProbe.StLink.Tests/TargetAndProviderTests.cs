// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Time.Testing;
using Periphery.DebugProbe.CortexM;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

public class TargetAndProviderTests
{
    [Theory]
    [InlineData(0xFBEFF8AAu, 0)]
    [InlineData(0xFBEFF8CCu, 2)]
    [InlineData(0xFBEFF800u, 1)]
    [InlineData(0xFBEFF8BBu, 1)]
    public void Read_protection_is_decoded_from_the_low_byte_of_OPTR(uint optr, int level)
    {
        Assert.Equal((ReadProtection)level, Stm32Registers.ReadProtectionOf(optr));
    }

    [Fact]
    public void Identity_registers_decode_as_measured_on_the_NUCLEO_G431KB()
    {
        // Measured 2026-10-12: IDCODE 0x20036468, flash size 0xFFFF0080.
        Assert.Equal(0x468, Stm32Registers.DeviceId(0x20036468));
        Assert.Equal(0x2003, Stm32Registers.RevisionId(0x20036468));
        Assert.Equal(128u * 1024, Stm32Registers.FlashSizeBytes(0xFFFF0080));
    }

    [Fact]
    public void The_allowlist_holds_the_STM32G431_alone_with_the_pack_algorithm()
    {
        var target = Assert.Single(Stm32Catalog.Targets.Values);
        Assert.Equal(0x468, target.DeviceId);
        Assert.Same(FlashAlgorithms.Stm32G43x4x128, target.Algorithm);
    }

    [Theory]
    [InlineData(@"USB\VID_0483&PID_374E&MI_00\9&1234ABCD&0&0000", 0x374E, "WinUSB", true)]   // V3 debug function
    [InlineData(@"USB\VID_0483&PID_374E&MI_02\9&1234ABCD&0&0002", 0x374E, "usbser", false)]  // its VCP
    [InlineData(@"USB\VID_0483&PID_374E\TESTSERIAL01", 0x374E, "usbccgp", false)] // composite parent
    [InlineData(@"USB\VID_0483&PID_3748\TESTSERIAL02", 0x3748, "WinUSB", true)]              // V2, one function
    [InlineData(@"USB\VID_0483&PID_DF11\TESTSERIAL03", 0xDF11, "WINUSB", false)]             // DFU bootloader
    public void The_provider_claims_only_the_debug_function(string id, int pid, string driver, bool claimed)
    {
        var device = new DeviceInfo
        {
            Id = id,
            VendorId = new HardwareId(0x0483),
            ProductId = new HardwareId((ushort)pid),
            Driver = driver,
        };

        Assert.Equal(claimed, new StLinkProvider().CanHandle(device));
    }

    [Fact]
    public void The_provider_is_probe_identified()
    {
        Assert.Equal(IdentificationMode.Probe, new StLinkProvider().Identification);
    }

    [Fact]
    public async Task Reset_and_halt_waits_for_the_reset_and_restores_DEMCR()
    {
        var target = new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, new FakeTimeProvider());
        var core = new CortexMDebug(target, target.Time);

        await core.ResetAndHaltAsync(CancellationToken.None);

        Assert.True(target.Reset);
        Assert.True(target.Halted);
        Assert.Equal(0u, await target.ReadWordAsync(CortexMRegisters.Demcr, CancellationToken.None) & 1);
    }

    [Fact]
    public async Task A_reset_that_never_halts_still_restores_DEMCR()
    {
        var target = new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, new FakeTimeProvider()) { HonoursResetCatch = false };
        var core = new CortexMDebug(target, target.Time);

        await Assert.ThrowsAsync<CortexMException>(() => core.ResetAndHaltAsync(CancellationToken.None));

        Assert.Equal(0u, await target.ReadWordAsync(CortexMRegisters.Demcr, CancellationToken.None) & 1);
    }

    [Fact]
    public async Task Core_registers_round_trip_through_DCRSR_and_DCRDR()
    {
        var target = new SimulatedStm32G431(FlashAlgorithms.Stm32G43x4x128, new FakeTimeProvider());
        var core = new CortexMDebug(target, target.Time);
        await core.HaltAsync(CancellationToken.None);

        await core.WriteRegisterAsync(CoreRegister.R9, 0x200001A4, CancellationToken.None);

        Assert.Equal(0x200001A4u, await core.ReadRegisterAsync(CoreRegister.R9, CancellationToken.None));
    }
}
