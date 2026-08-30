// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Treehopper;

/// <summary>
/// An immutable, composable snapshot of everything the host wants the board to be
/// configured as. The reconcile planner diffs this against the last applied config
/// to derive the minimal set of wire commands needed to reach it. (ADR-0052 DEC-003.)
/// </summary>
/// <remarks>
/// <para>
/// Blank (the default) represents "board as-delivered after a ConfigureDevice"
/// — all pins high-impedance, all peripherals disabled, LED off.
/// </para>
/// <para>
/// The reconcile path (<see cref="TreehopperBoard.ReconcileWithAsync"/>) applies
/// exactly the delta between this and the last applied config, so reconnect
/// re-sends only what is needed.
/// </para>
/// </remarks>
public sealed record BoardConfig
{
    /// <summary>
    /// The pre-init sentinel. When the board shell has no applied config yet, the
    /// planner uses this as the baseline, which prepends a device-configure (full
    /// firmware reset) command to the plan.
    /// </summary>
    public static readonly BoardConfig Blank = new();

    /// <summary>Per-pin desired configuration, keyed by pin number (0–19).</summary>
    public ImmutableDictionary<byte, PinConfig> Pins { get; init; }
        = ImmutableDictionary<byte, PinConfig>.Empty;

    /// <summary>Whether the on-board LED should be on.</summary>
    public bool LedOn { get; init; }

    /// <summary>I²C config, or <see langword="null"/> if disabled.</summary>
    public I2cConfig? I2c { get; init; }

    /// <summary>SPI config (marker), or <see langword="null"/> if disabled.</summary>
    public SpiConfig? Spi { get; init; }

    /// <summary>UART config, or <see langword="null"/> if disabled.</summary>
    public UartConfig? Uart { get; init; }

    /// <summary>Hardware-PWM config, or <see langword="null"/> if disabled.</summary>
    public PwmConfig? Pwm { get; init; }

    /// <summary>
    /// Active soft-PWM pins, keyed by pin number, with their 16-bit tick value
    /// (duty × 65535, or pulse-width ÷ 0.25 µs). Empty = no soft-PWM. The whole set is
    /// shipped as one aggregate packet whenever it changes; each pin is also driven as
    /// a push-pull output.
    /// </summary>
    public ImmutableDictionary<byte, ushort> SoftPwm { get; init; }
        = ImmutableDictionary<byte, ushort>.Empty;

    /// <summary>8080-style parallel-interface config, or <see langword="null"/> if disabled.</summary>
    public ParallelConfig? Parallel { get; init; }
}

/// <summary>Desired configuration for a single Treehopper pin.</summary>
/// <param name="Mode">The pin's electrical mode.</param>
/// <param name="DigitalValue">
/// For <see cref="PinMode.PushPullOutput"/>, the desired driven value.
/// Ignored for all other modes.
/// </param>
/// <param name="Reference">
/// For <see cref="PinMode.AnalogInput"/>, the ADC reference level. Ignored for all
/// other modes.
/// </param>
public sealed record PinConfig(
    PinMode Mode,
    bool DigitalValue = false,
    AdcReferenceLevel Reference = AdcReferenceLevel.Vref_3V3);

/// <summary>I²C module configuration.</summary>
/// <param name="SpeedKhz">Bus clock speed in kHz (≈62.5–16000).</param>
public sealed record I2cConfig(int SpeedKhz = 100);

/// <summary>SPI module configuration (marker — speed and mode are per-transaction).</summary>
public sealed record SpiConfig();

/// <summary>UART configuration.</summary>
/// <param name="Baud">Baud rate (≈7813–2 400 000). Ignored in 1-Wire mode.</param>
/// <param name="OpenDrainTx">When <c>true</c>, TX is open-drain rather than push-pull.</param>
/// <param name="Mode">Standard UART or 1-Wire bus mode.</param>
public sealed record UartConfig(
    int Baud = 9600, bool OpenDrainTx = false, UartMode Mode = UartMode.Uart);

/// <summary>
/// Hardware-PWM module configuration. Carries the full PWM state; the board
/// always receives the complete 9-byte packet when anything changes.
/// </summary>
/// <param name="Frequency">Base frequency shared by all three channels.</param>
/// <param name="EnableMode">
/// Cumulative channel-enable count (0 = none, 1 = pin 7, 2 = pin 7+8, 3 = all).
/// </param>
/// <param name="Duty7">Duty cycle for pin 7 (0.0–1.0).</param>
/// <param name="Duty8">Duty cycle for pin 8 (0.0–1.0).</param>
/// <param name="Duty9">Duty cycle for pin 9 (0.0–1.0).</param>
public sealed record PwmConfig(
    PwmFrequency Frequency,
    byte EnableMode = 0,
    double Duty7 = 0,
    double Duty8 = 0,
    double Duty9 = 0);

/// <summary>
/// 8080-style parallel-interface configuration. Carries the data-bus and control-pin
/// assignments; the firmware reserves these pins while the module is enabled.
/// </summary>
/// <param name="DataBusPins">The 4–16 data-bus pin numbers, least-significant first.</param>
/// <param name="RegisterSelectPin">The RS pin number, or -1 if unused.</param>
/// <param name="ReadWritePin">The R/W pin number, or -1 if unused.</param>
/// <param name="EnablePin">The E (enable/strobe) pin number, or -1 if unused.</param>
/// <param name="DelayMicroseconds">Settling delay after each bus strobe.</param>
public sealed record ParallelConfig(
    ImmutableArray<byte> DataBusPins,
    int RegisterSelectPin = -1,
    int ReadWritePin = -1,
    int EnablePin = -1,
    int DelayMicroseconds = 0);
