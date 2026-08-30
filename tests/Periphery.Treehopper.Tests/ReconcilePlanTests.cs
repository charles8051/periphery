using System.Collections.Immutable;
using System.Linq;
using Periphery.Treehopper.Wire;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Asserts that <see cref="TreehopperWire.Plan"/> emits the correct minimum set of
/// <see cref="Command"/>s for every transition scenario. Pure function, zero
/// hardware. (ADR-0052 DEC-003.)
/// </summary>
public class ReconcilePlanTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    private static IReadOnlyList<Command> Plan(BoardConfig desired, BoardConfig? applied = null)
        => TreehopperWire.Plan(desired, applied);

    private static BoardConfig WithPin(byte pin, PinMode mode, bool value = false)
        => BoardConfig.Blank with
        {
            Pins = ImmutableDictionary<byte, PinConfig>.Empty.Add(pin, new PinConfig(mode, value))
        };

    // ── Init / reconnect ───────────────────────────────────────────────

    [Fact]
    public void Plan_NullApplied_PrependsConfigureDevice()
    {
        var cmds = Plan(BoardConfig.Blank, applied: null);
        Assert.IsType<Command.ConfigureDevice>(cmds[0]);
    }

    [Fact]
    public void Plan_NonNullApplied_DoesNotEmitConfigureDevice()
    {
        var cmds = Plan(BoardConfig.Blank, BoardConfig.Blank);
        Assert.DoesNotContain(cmds, c => c is Command.ConfigureDevice);
    }

    [Fact]
    public void Plan_NullApplied_BlankDesired_EmitsOnlyConfigureDevice()
    {
        var cmds = Plan(BoardConfig.Blank, applied: null);
        var cmd = Assert.Single(cmds);
        Assert.IsType<Command.ConfigureDevice>(cmd);
    }

    // ── LED ───────────────────────────────────────────────────────────

    [Fact]
    public void Plan_LedOnChanged_EmitsSetLed()
    {
        var desired = BoardConfig.Blank with { LedOn = true };
        var cmds = Plan(desired, BoardConfig.Blank);

        var led = Assert.Single(cmds.OfType<Command.SetLed>());
        Assert.True(led.On);
    }

    [Fact]
    public void Plan_LedUnchanged_EmitsNothing()
    {
        var cfg = BoardConfig.Blank with { LedOn = true };
        var cmds = Plan(cfg, cfg);
        Assert.Empty(cmds);
    }

    // ── Pin config ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(PinMode.DigitalInput)]
    [InlineData(PinMode.PushPullOutput)]
    [InlineData(PinMode.AnalogInput)]
    public void Plan_PinAdded_EmitsConfigure(PinMode mode)
    {
        var desired = WithPin(0, mode);
        var cmds = Plan(desired, BoardConfig.Blank);

        var cfg = Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Equal((byte)0, cfg.Pin);
        Assert.Equal(mode, cfg.Mode);
    }

    [Fact]
    public void Plan_PinRemoved_EmitsDigitalInputRelease()
    {
        var applied = WithPin(3, PinMode.PushPullOutput);
        var cmds = Plan(BoardConfig.Blank, applied);

        var cfg = Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Equal((byte)3, cfg.Pin);
        Assert.Equal(PinMode.DigitalInput, cfg.Mode);
    }

    [Fact]
    public void Plan_PinModeUnchanged_EmitsNothing()
    {
        var cfg = WithPin(5, PinMode.AnalogInput);
        Assert.Empty(Plan(cfg, cfg));
    }

    [Fact]
    public void Plan_PinModeChanged_EmitsConfigure()
    {
        var applied = WithPin(1, PinMode.DigitalInput);
        var desired = WithPin(1, PinMode.AnalogInput);
        var cmds = Plan(desired, applied);

        var cfg = Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Equal(PinMode.AnalogInput, cfg.Mode);
    }

    [Fact]
    public void Plan_DigitalOutputValueChanged_EmitsWriteDigital()
    {
        var applied = WithPin(2, PinMode.PushPullOutput, false);
        var desired = WithPin(2, PinMode.PushPullOutput, true);
        var cmds = Plan(desired, applied);

        var write = Assert.Single(cmds.OfType<Command.WriteDigital>());
        Assert.Equal((byte)2, write.Pin);
        Assert.True(write.High);
    }

    [Fact]
    public void Plan_DigitalOutputModeNew_EmitsConfigureAndNoWriteDigital_WhenValueDefault()
    {
        // Adding a new push-pull pin with default (false) value should NOT emit WriteDigital
        // because DigitalValue=false is the same as the blank default (false).
        var desired = WithPin(4, PinMode.PushPullOutput, false);
        var cmds = Plan(desired, BoardConfig.Blank);

        Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Empty(cmds.OfType<Command.WriteDigital>());
    }

    [Fact]
    public void Plan_DigitalOutputModeNewWithHighValue_EmitsConfigureAndWrite()
    {
        var desired = WithPin(4, PinMode.PushPullOutput, true);
        var cmds = Plan(desired, BoardConfig.Blank);

        Assert.Single(cmds.OfType<Command.ConfigurePin>());
        var write = Assert.Single(cmds.OfType<Command.WriteDigital>());
        Assert.True(write.High);
    }

    // ── I²C ───────────────────────────────────────────────────────────

    [Fact]
    public void Plan_I2cEnabled_EmitsConfigureI2cEnable()
    {
        var desired = BoardConfig.Blank with { I2c = new I2cConfig(400) };
        var cmds = Plan(desired, BoardConfig.Blank);

        var i2c = Assert.Single(cmds.OfType<Command.ConfigureI2c>());
        Assert.True(i2c.Enable);
        Assert.Equal(400, i2c.SpeedKhz);
    }

    [Fact]
    public void Plan_I2cDisabled_EmitsConfigureI2cDisable()
    {
        var applied = BoardConfig.Blank with { I2c = new I2cConfig(100) };
        var cmds = Plan(BoardConfig.Blank, applied);

        var i2c = Assert.Single(cmds.OfType<Command.ConfigureI2c>());
        Assert.False(i2c.Enable);
    }

    [Fact]
    public void Plan_I2cSpeedChanged_EmitsConfigureI2cWithNewSpeed()
    {
        var applied = BoardConfig.Blank with { I2c = new I2cConfig(100) };
        var desired = BoardConfig.Blank with { I2c = new I2cConfig(400) };
        var cmds = Plan(desired, applied);

        var i2c = Assert.Single(cmds.OfType<Command.ConfigureI2c>());
        Assert.Equal(400, i2c.SpeedKhz);
    }

    [Fact]
    public void Plan_I2cUnchanged_EmitsNothing()
    {
        var cfg = BoardConfig.Blank with { I2c = new I2cConfig(100) };
        Assert.Empty(Plan(cfg, cfg));
    }

    // ── SPI ───────────────────────────────────────────────────────────

    [Fact]
    public void Plan_SpiEnabled_EmitsConfigureSpiTrue()
    {
        var desired = BoardConfig.Blank with { Spi = new SpiConfig() };
        var cmds = Plan(desired, BoardConfig.Blank);

        var spi = Assert.Single(cmds.OfType<Command.ConfigureSpi>());
        Assert.True(spi.Enable);
    }

    [Fact]
    public void Plan_SpiDisabled_EmitsConfigureSpiFalse()
    {
        var applied = BoardConfig.Blank with { Spi = new SpiConfig() };
        var cmds = Plan(BoardConfig.Blank, applied);

        var spi = Assert.Single(cmds.OfType<Command.ConfigureSpi>());
        Assert.False(spi.Enable);
    }

    // ── UART ──────────────────────────────────────────────────────────

    [Fact]
    public void Plan_UartEnabled_EmitsConfigureUartWithBaud()
    {
        var desired = BoardConfig.Blank with { Uart = new UartConfig(115200) };
        var cmds = Plan(desired, BoardConfig.Blank);

        var uart = Assert.Single(cmds.OfType<Command.ConfigureUart>());
        Assert.True(uart.Enable);
        Assert.Equal(115200, uart.Baud);
    }

    [Fact]
    public void Plan_UartDisabled_EmitsConfigureUartFalse()
    {
        var applied = BoardConfig.Blank with { Uart = new UartConfig(9600) };
        var cmds = Plan(BoardConfig.Blank, applied);

        var uart = Assert.Single(cmds.OfType<Command.ConfigureUart>());
        Assert.False(uart.Enable);
    }

    // ── PWM ───────────────────────────────────────────────────────────

    [Fact]
    public void Plan_PwmEnabled_EmitsConfigurePwmWithMode()
    {
        var desired = BoardConfig.Blank with
        {
            Pwm = new PwmConfig(PwmFrequency.Freq183Hz, EnableMode: 1, Duty7: 0.5)
        };
        var cmds = Plan(desired, BoardConfig.Blank);

        var pwm = Assert.Single(cmds.OfType<Command.ConfigurePwm>());
        Assert.Equal((byte)1, pwm.EnableMode);
        Assert.Equal(PwmFrequency.Freq183Hz, pwm.Frequency);
        Assert.Equal(0.5, pwm.Duty7);
    }

    [Fact]
    public void Plan_PwmDisabled_EmitsModeZero()
    {
        var applied = BoardConfig.Blank with
        {
            Pwm = new PwmConfig(PwmFrequency.Freq732Hz, EnableMode: 3, Duty7: 1, Duty8: 1, Duty9: 1)
        };
        var cmds = Plan(BoardConfig.Blank, applied);

        var pwm = Assert.Single(cmds.OfType<Command.ConfigurePwm>());
        Assert.Equal((byte)0, pwm.EnableMode);
    }

    // ── Reconnect scenario (the key ADR-0052 DEC-003 payoff) ─────────

    [Fact]
    public void Plan_ReconnectFromNull_ReappliesFullConfig()
    {
        // Simulate a board that had I2C + an output pin configured before disconnect.
        var desired = new BoardConfig
        {
            LedOn = true,
            I2c   = new I2cConfig(400),
            Pins  = ImmutableDictionary<byte, PinConfig>.Empty
                .Add(0, new PinConfig(PinMode.PushPullOutput, true))
        };

        var cmds = Plan(desired, applied: null);

        Assert.IsType<Command.ConfigureDevice>(cmds[0]); // reconnect init
        Assert.Contains(cmds, c => c is Command.SetLed { On: true });
        Assert.Contains(cmds, c => c is Command.ConfigurePin { Pin: 0, Mode: PinMode.PushPullOutput });
        Assert.Contains(cmds, c => c is Command.WriteDigital { Pin: 0, High: true });
        Assert.Contains(cmds, c => c is Command.ConfigureI2c { Enable: true, SpeedKhz: 400 });
    }

    // ── ADC reference level ────────────────────────────────────────────

    [Fact]
    public void Plan_AnalogReferenceChanged_EmitsConfigurePin()
    {
        var applied = BoardConfig.Blank with
        {
            Pins = ImmutableDictionary<byte, PinConfig>.Empty
                .Add(2, new PinConfig(PinMode.AnalogInput, Reference: AdcReferenceLevel.Vref_3V3))
        };
        var desired = BoardConfig.Blank with
        {
            Pins = ImmutableDictionary<byte, PinConfig>.Empty
                .Add(2, new PinConfig(PinMode.AnalogInput, Reference: AdcReferenceLevel.Vref_2V4))
        };
        var cmds = Plan(desired, applied);

        var cfg = Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Equal(PinMode.AnalogInput, cfg.Mode);
        Assert.Equal(AdcReferenceLevel.Vref_2V4, cfg.Reference);
    }

    [Fact]
    public void Plan_AnalogReferenceUnchanged_EmitsNothing()
    {
        var cfg = BoardConfig.Blank with
        {
            Pins = ImmutableDictionary<byte, PinConfig>.Empty
                .Add(2, new PinConfig(PinMode.AnalogInput, Reference: AdcReferenceLevel.Vref_1V65))
        };
        Assert.Empty(Plan(cfg, cfg));
    }

    // ── UART 1-Wire ────────────────────────────────────────────────────

    [Fact]
    public void Plan_UartOneWireMode_EmitsConfigureUartOneWire()
    {
        var desired = BoardConfig.Blank with { Uart = new UartConfig(Mode: UartMode.OneWire) };
        var cmds = Plan(desired, BoardConfig.Blank);

        var uart = Assert.Single(cmds.OfType<Command.ConfigureUart>());
        Assert.True(uart.Enable);
        Assert.Equal(UartMode.OneWire, uart.Mode);
    }

    // ── Soft-PWM ───────────────────────────────────────────────────────

    [Fact]
    public void Plan_SoftPwmAdded_EmitsPushPullThenAggregateConfig()
    {
        var desired = BoardConfig.Blank with
        {
            SoftPwm = ImmutableDictionary<byte, ushort>.Empty.Add(5, 32768)
        };
        var cmds = Plan(desired, BoardConfig.Blank).ToList();

        // The pin must become a push-pull output before the soft-PWM packet ships.
        var pin = Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Equal((byte)5, pin.Pin);
        Assert.Equal(PinMode.PushPullOutput, pin.Mode);

        var soft = Assert.Single(cmds.OfType<Command.ConfigureSoftPwm>());
        Assert.Equal((ushort)32768, soft.Pins[5]);

        int pinIdx  = cmds.FindIndex(c => c is Command.ConfigurePin);
        int softIdx = cmds.FindIndex(c => c is Command.ConfigureSoftPwm);
        Assert.True(pinIdx >= 0 && pinIdx < softIdx);
    }

    [Fact]
    public void Plan_SoftPwmRemoved_ReleasesPinAndDisables()
    {
        var applied = BoardConfig.Blank with
        {
            SoftPwm = ImmutableDictionary<byte, ushort>.Empty.Add(5, 32768)
        };
        var cmds = Plan(BoardConfig.Blank, applied);

        var pin = Assert.Single(cmds.OfType<Command.ConfigurePin>());
        Assert.Equal((byte)5, pin.Pin);
        Assert.Equal(PinMode.DigitalInput, pin.Mode);

        var soft = Assert.Single(cmds.OfType<Command.ConfigureSoftPwm>());
        Assert.Empty(soft.Pins);
    }

    [Fact]
    public void Plan_SoftPwmUnchanged_EmitsNothing()
    {
        var cfg = BoardConfig.Blank with
        {
            SoftPwm = ImmutableDictionary<byte, ushort>.Empty.Add(5, 100)
        };
        Assert.Empty(Plan(cfg, cfg));
    }

    [Fact]
    public void Plan_SoftPwmDutyChanged_EmitsAggregateConfigOnly()
    {
        var applied = BoardConfig.Blank with
        {
            SoftPwm = ImmutableDictionary<byte, ushort>.Empty.Add(5, 100)
        };
        var desired = BoardConfig.Blank with
        {
            SoftPwm = ImmutableDictionary<byte, ushort>.Empty.Add(5, 200)
        };
        var cmds = Plan(desired, applied);

        // The pin is already push-pull, so only the aggregate packet re-ships.
        Assert.Empty(cmds.OfType<Command.ConfigurePin>());
        var soft = Assert.Single(cmds.OfType<Command.ConfigureSoftPwm>());
        Assert.Equal((ushort)200, soft.Pins[5]);
    }

    // ── Parallel interface ─────────────────────────────────────────────

    [Fact]
    public void Plan_ParallelEnabled_EmitsConfigureParallelTrue()
    {
        var desired = BoardConfig.Blank with
        {
            Parallel = new ParallelConfig(ImmutableArray.Create<byte>(8, 9, 10, 11), 3, 4, 6)
        };
        var cmds = Plan(desired, BoardConfig.Blank);

        var par = Assert.Single(cmds.OfType<Command.ConfigureParallel>());
        Assert.True(par.Enable);
        Assert.Equal(4, par.DataBusPins.Length);
    }

    [Fact]
    public void Plan_ParallelDisabled_EmitsConfigureParallelFalse()
    {
        var applied = BoardConfig.Blank with
        {
            Parallel = new ParallelConfig(ImmutableArray.Create<byte>(8, 9, 10, 11))
        };
        var cmds = Plan(BoardConfig.Blank, applied);

        var par = Assert.Single(cmds.OfType<Command.ConfigureParallel>());
        Assert.False(par.Enable);
    }

    [Fact]
    public void Plan_ParallelUnchanged_EmitsNothing()
    {
        var cfg = BoardConfig.Blank with
        {
            Parallel = new ParallelConfig(ImmutableArray.Create<byte>(8, 9, 10, 11), 3, 4, 6, 2)
        };
        Assert.Empty(Plan(cfg, cfg));
    }
}
