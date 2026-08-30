using System;
using System.Collections.Immutable;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Asserts that <see cref="TreehopperWire.Encode"/> produces byte-exact wire
/// packets for every <see cref="Command"/> variant. These are the cheapest tests:
/// pure function, zero hardware. (ADR-0052 DEC-001.)
/// </summary>
public class WireEncodeTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    private static (byte Ep, byte[] Bytes) Enc(Command cmd) => TreehopperWire.Encode(cmd);

    private static void AssertPacket(byte expectedEndpoint, byte[] expectedBytes, Command cmd)
    {
        var (ep, bytes) = Enc(cmd);
        Assert.Equal(expectedEndpoint, ep);
        Assert.Equal(expectedBytes, bytes);
    }

    private const byte PinEp  = 0x01;
    private const byte PerifEp = 0x02;

    // ── Pin-config endpoint (0x01) ────────────────────────────────────

    [Theory]
    [InlineData(PinMode.DigitalInput,    1)]
    [InlineData(PinMode.PushPullOutput,  2)]
    [InlineData(PinMode.OpenDrainOutput, 3)]
    [InlineData(PinMode.AnalogInput,     4)]
    public void ConfigurePin_EncodesModeToPinConfigCommand(PinMode mode, byte cmdByte)
        => AssertPacket(PinEp,
            new byte[] { 5, cmdByte, 0, 0, 0, 0 },
            new Command.ConfigurePin(5, mode));

    [Theory]
    [InlineData(true,  1)]
    [InlineData(false, 0)]
    public void WriteDigital_EncodesValueByte(bool high, byte val)
        => AssertPacket(PinEp,
            new byte[] { 3, 5, val, 0, 0, 0 },
            new Command.WriteDigital(3, high));

    // ── Peripheral-config endpoint (0x02) ─────────────────────────────

    [Fact]
    public void ConfigureDevice_EncodesTwoBytes()
        => AssertPacket(PerifEp, new byte[] { 0x01, 0x00 }, new Command.ConfigureDevice());

    [Theory]
    [InlineData(true,  0x01)]
    [InlineData(false, 0x00)]
    public void SetLed_EncodesOnOffByte(bool on, byte val)
        => AssertPacket(PerifEp, new byte[] { 0x0E, val }, new Command.SetLed(on));

    [Fact]
    public void Reboot_EncodesOpcode()
        => AssertPacket(PerifEp, new byte[] { 0x0C }, new Command.Reboot());

    // ── I²C ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100,  243)] // round(256 − 4000/300) = 243
    [InlineData(400,  253)] // round(256 − 4000/1200) = 253
    public void I2cRateByte_MatchesFirmwareFormula(int khz, int expected)
        => Assert.Equal((byte)expected, TreehopperWire.I2cRateByte(khz));

    [Fact]
    public void ConfigureI2c_Enable_EncodesCmdEnableRate()
        => AssertPacket(PerifEp,
            new byte[] { 0x04, 0x01, 243 },
            new Command.ConfigureI2c(true, 100));

    [Fact]
    public void ConfigureI2c_Disable_EncodesZeroRate()
        => AssertPacket(PerifEp, new byte[] { 0x04, 0x00, 0x00 }, new Command.ConfigureI2c(false));

    [Fact]
    public void I2cTransaction_FramesAddressLengthsPayload()
        => AssertPacket(PerifEp,
            new byte[] { 0x06, 0x50, 0x02, 0x04, 0xDE, 0xAD },
            new Command.I2cTransaction(0x50, new byte[] { 0xDE, 0xAD }.AsMemory(), 4));

    // ── SPI ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(24.0, 0)]
    [InlineData(12.0, 1)]
    [InlineData( 6.0, 3)]   // exactly 6 MHz — the safe upper boundary, not bumped
    [InlineData( 0.8, 29)]  // exactly 0.8 MHz — the safe lower boundary, not bumped
    public void SpiClockByte_MatchesDivisorFormula(double mhz, int expected)
        => Assert.Equal((byte)expected, TreehopperWire.SpiClockByte(mhz));

    // The EFM8 SPI FIFO can lock up under heavy USB traffic when clocked in the
    // (0.8, 6) MHz band (a documented silicon bug), so every request in that band is
    // rounded up to the safe 6 MHz boundary (divisor 3) — verbatim from the original
    // SDK's HardwareSpi.SendReceiveAsync. Clocking the APA102 strip at 4 MHz without
    // this guard wedged and bricked boards. The default (allowDangerBand: false) is
    // the production path; the codec reads no environment (ADR-0052 DEC-001).
    [Theory]
    [InlineData(0.81)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(5.999)]
    public void SpiClockByte_SiliconBugBand_RoundsUpToSafe6MHz(double mhz)
        => Assert.Equal((byte)3, TreehopperWire.SpiClockByte(mhz, allowDangerBand: false));

    // DEBUG-ONLY danger-band bypass: when the shell sets allowDangerBand (from
    // TREEHOPPER_SPI_DANGER_BAND=1), the requested speed is clocked verbatim instead
    // of being rounded up — the lock-up-reproduction path. The decision now lives in
    // the shell; the codec just honours the flag deterministically.
    [Theory]
    [InlineData(1.0, 23)]   // 24/1 - 1 = 23, NOT rounded up to divisor 3
    [InlineData(4.0,  5)]   // 24/4 - 1 = 5
    [InlineData(2.0, 11)]   // 24/2 - 1 = 11
    public void SpiClockByte_DangerBandAllowed_ClocksRequestedSpeedVerbatim(double mhz, int expected)
        => Assert.Equal((byte)expected, TreehopperWire.SpiClockByte(mhz, allowDangerBand: true));

    [Fact]
    public void SpiMode_ValuesAreMcuRegisterEncoding()
    {
        Assert.Equal((byte)0x00, (byte)SpiMode.Mode00);
        Assert.Equal((byte)0x20, (byte)SpiMode.Mode01);
        Assert.Equal((byte)0x10, (byte)SpiMode.Mode10);
        Assert.Equal((byte)0x30, (byte)SpiMode.Mode11);
    }

    [Fact]
    public void ConfigureSpi_Enable_EncodesCmdPlusOne()
        => AssertPacket(PerifEp, new byte[] { 0x05, 0x01 }, new Command.ConfigureSpi(true));

    [Fact]
    public void ConfigureSpi_Disable_EncodesCmdPlusZero()
        => AssertPacket(PerifEp, new byte[] { 0x05, 0x00 }, new Command.ConfigureSpi(false));

    [Fact]
    public void SpiTransaction_NoChipSelect_FramesHeaderAndPayload()
        // [cmd, cs=0xFF, csMode=0, clk=3, mode=0, burst=0, len=2, 0x01, 0x02]
        => AssertPacket(PerifEp,
            new byte[] { 0x07, 0xFF, 0x00, 0x03, 0x00, 0x00, 0x02, 0x01, 0x02 },
            new Command.SpiTransaction(
                new byte[] { 0x01, 0x02 }.AsMemory(),
                ChipSelectPin: -1, ChipSelectMode: 0,
                SpeedMhz: 6, Mode: SpiMode.Mode00, Burst: 0));

    [Fact]
    public void SpiTransaction_DangerBandDefault_RoundsClockByteUp()
        // 4 MHz with the default (AllowDangerBand: false) is rounded to the safe
        // 6 MHz boundary -> clk divisor byte 3. This is the production behaviour.
        => AssertPacket(PerifEp,
            new byte[] { 0x07, 0xFF, 0x00, 0x03, 0x00, 0x00, 0x01, 0xAA },
            new Command.SpiTransaction(
                new byte[] { 0xAA }.AsMemory(),
                ChipSelectPin: -1, ChipSelectMode: 0,
                SpeedMhz: 4, Mode: SpiMode.Mode00, Burst: 0));

    [Fact]
    public void SpiTransaction_DangerBandAllowed_EncodesRequestedClockVerbatim()
        // With AllowDangerBand: true (the path the shell takes when
        // TREEHOPPER_SPI_DANGER_BAND=1), 4 MHz is clocked verbatim: 24/4 - 1 = 5.
        // Proves the flag flows through Encode -> SpiTransactionBytes -> SpiClockByte.
        => AssertPacket(PerifEp,
            new byte[] { 0x07, 0xFF, 0x00, 0x05, 0x00, 0x00, 0x01, 0xAA },
            new Command.SpiTransaction(
                new byte[] { 0xAA }.AsMemory(),
                ChipSelectPin: -1, ChipSelectMode: 0,
                SpeedMhz: 4, Mode: SpiMode.Mode00, Burst: 0,
                AllowDangerBand: true));

    // ── UART ──────────────────────────────────────────────────────────

    [Fact]
    public void UartTimer_9600_UsesPrescaledClock()
    {
        var (timer, prescaler) = TreehopperWire.UartTimer(9600);
        Assert.Equal((byte)48, timer);
        Assert.True(prescaler);
    }

    [Fact]
    public void ConfigureUart_Enable_9600_EncodesTimerAndFlags()
        => AssertPacket(PerifEp,
            new byte[] { 0x03, 0x01, 48, 0x01, 0x00 },
            new Command.ConfigureUart(true, 9600));

    [Fact]
    public void ConfigureUart_Disable_EncodesZeroEnable()
        => AssertPacket(PerifEp, new byte[] { 0x03, 0x00 }, new Command.ConfigureUart(false));

    [Fact]
    public void UartTransmit_FramesLengthAndPayload()
        => AssertPacket(PerifEp,
            new byte[] { 0x08, 0x00, 0x02, 0x41, 0x42 },
            new Command.UartTransmit(new byte[] { 0x41, 0x42 }.AsMemory()));

    [Fact]
    public void UartReceive_IsTwoByteRequest()
        => AssertPacket(PerifEp, new byte[] { 0x08, 0x01 }, new Command.UartReceive());

    // ── PWM ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0,     0)]
    [InlineData(1.0, 65535)]
    [InlineData(0.5, 32768)]
    public void PwmDutyRegister_Scales16Bit(double duty, int expected)
        => Assert.Equal((ushort)expected, TreehopperWire.PwmDutyRegister(duty));

    [Fact]
    public void ConfigurePwm_FramesNineBytesLittleEndianDuty()
        // [cmd, mode=1, freq=0, pin7 0x8000 LE, pin8 0, pin9 0]
        => AssertPacket(PerifEp,
            new byte[] { 0x02, 0x01, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00 },
            new Command.ConfigurePwm(
                EnableMode: 1, Frequency: PwmFrequency.Freq732Hz,
                Duty7: 0.5, Duty8: 0, Duty9: 0));

    [Fact]
    public void ConfigurePwm_AllDisabled_SendsModeZero()
        => AssertPacket(PerifEp,
            new byte[] { 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0 },
            new Command.ConfigurePwm(0, PwmFrequency.Freq183Hz, 0, 0, 0));

    // ── Response lengths ──────────────────────────────────────────────

    [Fact]
    public void ResponseLength_I2cTransaction_IsOneStatusPlusReadLen()
        => Assert.Equal(3, TreehopperWire.ResponseLength(
            new Command.I2cTransaction(0x50, ReadOnlyMemory<byte>.Empty, 2)));

    [Fact]
    public void ResponseLength_SpiTransaction_MirrorsWriteLength()
        => Assert.Equal(2, TreehopperWire.ResponseLength(
            new Command.SpiTransaction(
                new byte[2].AsMemory(), -1, 0, 6, SpiMode.Mode00, 0)));

    [Fact]
    public void ResponseLength_UartTransmit_IsOneAckByte()
        => Assert.Equal(1, TreehopperWire.ResponseLength(
            new Command.UartTransmit(new byte[] { 0x42 }.AsMemory())));

    [Fact]
    public void ResponseLength_UartReceive_Is33Bytes()
        => Assert.Equal(33, TreehopperWire.ResponseLength(new Command.UartReceive()));

    [Fact]
    public void ResponseLength_ConfigCommands_AreZero()
    {
        Assert.Equal(0, TreehopperWire.ResponseLength(new Command.SetLed(true)));
        Assert.Equal(0, TreehopperWire.ResponseLength(new Command.ConfigureI2c(true)));
        Assert.Equal(0, TreehopperWire.ResponseLength(new Command.ConfigureSpi(true)));
    }

    // ── ADC reference (rides in byte[2] of the analog pin-config packet) ─

    [Theory]
    [InlineData(AdcReferenceLevel.Vref_3V3,  0)]
    [InlineData(AdcReferenceLevel.Vref_1V65, 1)]
    [InlineData(AdcReferenceLevel.Vref_1V85, 2)]
    [InlineData(AdcReferenceLevel.Vref_2V4,  3)]
    [InlineData(AdcReferenceLevel.Vref_3V7,  5)]
    public void ConfigurePin_Analog_EncodesReferenceInByte2(AdcReferenceLevel reference, byte refByte)
        => AssertPacket(PinEp,
            new byte[] { 5, 4, refByte, 0, 0, 0 },
            new Command.ConfigurePin(5, PinMode.AnalogInput, reference));

    [Fact]
    public void ConfigurePin_NonAnalog_IgnoresReference()
        => AssertPacket(PinEp,
            new byte[] { 5, 1, 0, 0, 0, 0 },
            new Command.ConfigurePin(5, PinMode.DigitalInput, AdcReferenceLevel.Vref_1V85));

    // ── SPI chip-select & burst modes ──────────────────────────────────

    [Fact]
    public void SpiTransaction_ChipSelect_EncodesPinAndMode()
        => AssertPacket(PerifEp,
            new byte[] { 0x07, 0x09, 0x04, 0x03, 0x00, 0x00, 0x01, 0xAB },
            new Command.SpiTransaction(
                new byte[] { 0xAB }.AsMemory(),
                ChipSelectPin: 9, ChipSelectMode: (byte)ChipSelectMode.PulseLowAtBeginning,
                SpeedMhz: 6, Mode: SpiMode.Mode00, Burst: 0));

    [Fact]
    public void SpiTransaction_BurstTx_FramesPayload_NoResponse()
    {
        var cmd = new Command.SpiTransaction(
            new byte[] { 0x01, 0x02 }.AsMemory(), -1, 0, 6, SpiMode.Mode00, Burst: 1);
        AssertPacket(PerifEp,
            new byte[] { 0x07, 0xFF, 0x00, 0x03, 0x00, 0x01, 0x02, 0x01, 0x02 }, cmd);
        Assert.Equal(0, TreehopperWire.ResponseLength(cmd));
    }

    [Fact]
    public void SpiTransaction_BurstRx_FramesHeaderOnly_ReadsLength()
    {
        var cmd = new Command.SpiTransaction(
            new byte[2].AsMemory(), -1, 0, 6, SpiMode.Mode00, Burst: 2);
        // header only — no MOSI payload; header[6] still carries the read length
        AssertPacket(PerifEp,
            new byte[] { 0x07, 0xFF, 0x00, 0x03, 0x00, 0x02, 0x02 }, cmd);
        Assert.Equal(2, TreehopperWire.ResponseLength(cmd));
    }

    // ── UART 1-Wire ────────────────────────────────────────────────────

    [Fact]
    public void ConfigureUart_OneWire_EncodesModeTwo()
        => AssertPacket(PerifEp, new byte[] { 0x03, 0x02 },
            new Command.ConfigureUart(true, Mode: UartMode.OneWire));

    [Fact]
    public void UartReceive_OneWire_AppendsByteCount()
        => AssertPacket(PerifEp, new byte[] { 0x08, 0x01, 0x05 }, new Command.UartReceive(5));

    [Fact]
    public void OneWireReset_EncodesSubCommandTwo_ReadsOneByte()
    {
        var cmd = new Command.OneWireReset();
        AssertPacket(PerifEp, new byte[] { 0x08, 0x02 }, cmd);
        Assert.Equal(1, TreehopperWire.ResponseLength(cmd));
    }

    [Fact]
    public void OneWireScan_EncodesSubCommandThree()
        => AssertPacket(PerifEp, new byte[] { 0x08, 0x03 }, new Command.OneWireScan());

    // ── Soft-PWM (delta-timing aggregate, hand-computed vs the original SDK) ─

    [Fact]
    public void SoftPwm_Empty_DisablesWithZeroCount()
        => AssertPacket(PerifEp, new byte[] { 0x09, 0x00 },
            new Command.ConfigureSoftPwm(ImmutableDictionary<byte, ushort>.Empty));

    [Fact]
    public void SoftPwm_SinglePin_EncodesDeltaTimingSchedule()
    {
        // pin 5 at 50% (ticks 32768) → [cmd, count=2, (0, 0x7FFF), (pin5, 0x8000)]
        var pins = ImmutableDictionary<byte, ushort>.Empty.Add(5, 32768);
        AssertPacket(PerifEp,
            new byte[] { 0x09, 0x02, 0x00, 0x7F, 0xFF, 0x05, 0x80, 0x00 },
            new Command.ConfigureSoftPwm(pins));
    }

    [Fact]
    public void SoftPwm_TwoPins_OrdersByTicksAndDeltaEncodes()
    {
        var pins = ImmutableDictionary<byte, ushort>.Empty.Add(7, 40000).Add(3, 10000);
        AssertPacket(PerifEp,
            new byte[] { 0x09, 0x03, 0x00, 0xD8, 0xEF, 0x03, 0x8A, 0xCF, 0x07, 0x9C, 0x40 },
            new Command.ConfigureSoftPwm(pins));
    }

    // ── Parallel interface ─────────────────────────────────────────────

    [Fact]
    public void ConfigureParallel_Enable_FramesBusAndControlPins()
        => AssertPacket(PerifEp,
            new byte[] { 0x0F, 0x01, 0x05, 0x04, 0x03, 0x04, 0x06, 0x08, 0x09, 0x0A, 0x0B },
            new Command.ConfigureParallel(
                Enable: true, DelayMicroseconds: 5,
                DataBusPins: ImmutableArray.Create<byte>(8, 9, 10, 11),
                RegisterSelectPin: 3, ReadWritePin: 4, EnablePin: 6));

    [Fact]
    public void ConfigureParallel_UnusedControlPin_EncodesAs0xFF()
        => AssertPacket(PerifEp,
            new byte[] { 0x0F, 0x01, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x08, 0x09, 0x0A, 0x0B },
            new Command.ConfigureParallel(
                Enable: true, DelayMicroseconds: 0,
                DataBusPins: ImmutableArray.Create<byte>(8, 9, 10, 11),
                RegisterSelectPin: -1, ReadWritePin: -1, EnablePin: -1));

    [Fact]
    public void ParallelWrite_Command_8BitBus_OneBytePerWord()
        => AssertPacket(PerifEp,
            new byte[] { 0x10, 0x00, 0x02, 0x38, 0x0C },
            new Command.ParallelWrite(IsData: false, ImmutableArray.Create<uint>(0x38, 0x0C), BusWidth: 4));

    [Fact]
    public void ParallelWrite_Data_16BitBus_BigEndianWords()
        => AssertPacket(PerifEp,
            new byte[] { 0x10, 0x02, 0x01, 0xAB, 0xCD },
            new Command.ParallelWrite(IsData: true, ImmutableArray.Create<uint>(0xABCD), BusWidth: 16));

    // ── Identity / lifecycle ───────────────────────────────────────────

    [Fact]
    public void UpdateName_FramesLengthAndUtf8()
        => AssertPacket(PerifEp, new byte[] { 0x0B, 0x02, 0x41, 0x42 }, new Command.UpdateName("AB"));

    [Fact]
    public void UpdateSerial_FramesLengthAndUtf8()
        => AssertPacket(PerifEp, new byte[] { 0x0A, 0x02, 0x41, 0x42 }, new Command.UpdateSerial("AB"));

    [Fact]
    public void EnterBootloader_EncodesOpcode()
        => AssertPacket(PerifEp, new byte[] { 0x0D }, new Command.EnterBootloader());

    [Fact]
    public void ResponseLength_NewCommands_AreZero()
    {
        Assert.Equal(0, TreehopperWire.ResponseLength(
            new Command.ConfigureSoftPwm(ImmutableDictionary<byte, ushort>.Empty)));
        Assert.Equal(0, TreehopperWire.ResponseLength(new Command.EnterBootloader()));
        Assert.Equal(0, TreehopperWire.ResponseLength(new Command.UpdateName("x")));
        Assert.Equal(0, TreehopperWire.ResponseLength(new Command.OneWireScan()));
        Assert.Equal(0, TreehopperWire.ResponseLength(
            new Command.ParallelWrite(false, ImmutableArray<uint>.Empty, 8)));
    }
}
