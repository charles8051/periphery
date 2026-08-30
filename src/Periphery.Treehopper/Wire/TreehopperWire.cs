// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Periphery.Treehopper.Wire;

/// <summary>
/// Pure, total, I/O-free codec and planner for the Treehopper EFM8 wire protocol.
/// Every function here is deterministic: same input → same output, no side effects.
/// (ADR-0052 DEC-001 / DEC-002 / DEC-003.)
/// </summary>
/// <remarks>
/// Wire-byte values and packet shapes are preserved verbatim from the original
/// Treehopper SDK for firmware compatibility (ADR-0039 constraint).
/// </remarks>
internal static class TreehopperWire
{
    // ── Endpoints ──────────────────────────────────────────────────────

    /// <summary>OUT endpoint for pin-mode / pin-value config (6-byte packets).</summary>
    public const byte PinConfigEndpoint = 0x01;

    /// <summary>OUT endpoint for peripheral config + transactions (LED, I²C/SPI/UART/PWM).</summary>
    public const byte PeripheralConfigEndpoint = 0x02;

    /// <summary>IN endpoint streaming the 41-byte pin-state report.</summary>
    public const byte PinReportEndpoint = 0x81;

    /// <summary>IN endpoint for peripheral (I²C/SPI/UART) responses.</summary>
    public const byte PeripheralResponseEndpoint = 0x82;

    // ── Protocol constants ─────────────────────────────────────────────

    /// <summary>Max bulk-packet size — transfers larger than this are chunked by the shell.</summary>
    public const int MaxPacket = 64;

    /// <summary>Number of I/O pins on a Treehopper board.</summary>
    public const int PinCount = 20;

    /// <summary>Length of a pin-state report: 1 ID byte + 20 pins × 2 bytes.</summary>
    public const int PinReportLength = 1 + PinCount * 2;

    /// <summary>ADC full-scale divisor (12-bit ADC, full-scale = 4092).</summary>
    public const double AdcDivisor = 4092.0;

    /// <summary>I²C success sentinel — the leading status byte of a successful response.</summary>
    public const byte I2cSuccess = 0xFF;

    /// <summary>Length of a single 1-Wire ROM-search response packet (1 status + 8 ROM bytes).</summary>
    public const int OneWireRomPacketLength = 9;

    /// <summary>The status byte that terminates a 1-Wire ROM search.</summary>
    public const byte OneWireScanTerminator = 0xFF;

    // ── Device command bytes ───────────────────────────────────────────

    private const byte CmdConfigureDevice      = 0x01;
    private const byte CmdPwmConfig            = 0x02;
    private const byte CmdUartConfig           = 0x03;
    private const byte CmdI2cConfig            = 0x04;
    private const byte CmdSpiConfig            = 0x05;
    private const byte CmdI2cTransaction       = 0x06;
    private const byte CmdSpiTransaction       = 0x07;
    private const byte CmdUartTransaction      = 0x08;
    private const byte CmdSoftPwmConfig        = 0x09;
    private const byte CmdFirmwareUpdateSerial = 0x0A;
    private const byte CmdFirmwareUpdateName   = 0x0B;
    private const byte CmdReboot               = 0x0C;
    private const byte CmdEnterBootloader      = 0x0D;
    private const byte CmdLedConfig            = 0x0E;
    private const byte CmdParallelConfig       = 0x0F;
    private const byte CmdParallelTransaction  = 0x10;

    // ── Pin-config command bytes ───────────────────────────────────────

    private const byte PinCmdDigitalInput    = 1;
    private const byte PinCmdPushPullOutput  = 2;
    private const byte PinCmdOpenDrainOutput = 3;
    private const byte PinCmdAnalogInput     = 4;
    private const byte PinCmdSetDigitalValue = 5;

    // ── UART config sub-values ─────────────────────────────────────────

    private const byte UartConfigDisabled = 0;
    private const byte UartConfigStandard = 1;
    private const byte UartConfigOneWire  = 2;

    // ── UART transaction sub-commands ──────────────────────────────────

    private const byte UartCmdTransmit     = 0;
    private const byte UartCmdReceive      = 1;
    private const byte UartCmdOneWireReset = 2;
    private const byte UartCmdOneWireScan  = 3;

    // ── Parallel transaction sub-commands ──────────────────────────────

    private const byte ParallelCmdWriteCommand = 0;
    private const byte ParallelCmdWriteData    = 2;

    // ── SPI burst-mode bytes ───────────────────────────────────────────

    private const byte SpiBurstTx = 1;
    private const byte SpiBurstRx = 2;

    // ── Encode ─────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="command"/> to wire bytes and returns the target
    /// USB endpoint and the packet payload. Pure: no allocations beyond the
    /// returned array, no IO, no clock.
    /// </summary>
    public static (byte Endpoint, byte[] Bytes) Encode(Command command) => command switch
    {
        Command.ConfigurePin(var pin, var mode, var reference)
            => (PinConfigEndpoint, PinModeBytes(pin, mode, reference)),

        Command.WriteDigital(var pin, var high)
            => (PinConfigEndpoint, [pin, PinCmdSetDigitalValue, (byte)(high ? 1 : 0), 0, 0, 0]),

        Command.ConfigureDevice
            => (PeripheralConfigEndpoint, [CmdConfigureDevice, 0x00]),

        Command.SetLed(var on)
            => (PeripheralConfigEndpoint, [CmdLedConfig, (byte)(on ? 1 : 0)]),

        Command.ConfigureI2c(var enable, var speedKhz)
            => (PeripheralConfigEndpoint,
                [CmdI2cConfig, (byte)(enable ? 1 : 0), enable ? I2cRateByte(speedKhz) : (byte)0]),

        Command.I2cTransaction(var addr, var tx, var readLen)
            => (PeripheralConfigEndpoint, I2cTransactionBytes(addr, tx.Span, readLen)),

        Command.ConfigureSpi(var enable)
            => (PeripheralConfigEndpoint, [CmdSpiConfig, (byte)(enable ? 1 : 0)]),

        Command.SpiTransaction(var tx, var csPin, var csMode, var mhz, var mode, var burst, var allowDanger)
            => (PeripheralConfigEndpoint, SpiTransactionBytes(tx.Span, csPin, csMode, mhz, mode, burst, allowDanger)),

        Command.ConfigureUart(var enable, var baud, var openDrain, var uartMode)
            => (PeripheralConfigEndpoint, UartConfigBytes(enable, baud, openDrain, uartMode)),

        Command.UartTransmit(var data)
            => (PeripheralConfigEndpoint, UartTransmitBytes(data.Span)),

        Command.UartReceive(var oneWireBytes)
            => (PeripheralConfigEndpoint, oneWireBytes > 0
                ? [CmdUartTransaction, UartCmdReceive, (byte)oneWireBytes]
                : [CmdUartTransaction, UartCmdReceive]),

        Command.OneWireReset
            => (PeripheralConfigEndpoint, [CmdUartTransaction, UartCmdOneWireReset]),

        Command.OneWireScan
            => (PeripheralConfigEndpoint, [CmdUartTransaction, UartCmdOneWireScan]),

        Command.ConfigurePwm(var em, var freq, var d7, var d8, var d9)
            => (PeripheralConfigEndpoint, PwmConfigBytes(em, freq, d7, d8, d9)),

        Command.ConfigureSoftPwm(var pins)
            => (PeripheralConfigEndpoint, SoftPwmBytes(pins)),

        Command.ConfigureParallel(var enable, var delay, var bus, var rs, var rw, var en)
            => (PeripheralConfigEndpoint, ParallelConfigBytes(enable, delay, bus, rs, rw, en)),

        Command.ParallelWrite(var isData, var words, var busWidth)
            => (PeripheralConfigEndpoint, ParallelWriteBytes(isData, words, busWidth)),

        Command.UpdateName(var name)
            => (PeripheralConfigEndpoint, IdentityBytes(CmdFirmwareUpdateName, name)),

        Command.UpdateSerial(var serial)
            => (PeripheralConfigEndpoint, IdentityBytes(CmdFirmwareUpdateSerial, serial)),

        Command.Reboot
            => (PeripheralConfigEndpoint, [CmdReboot]),

        Command.EnterBootloader
            => (PeripheralConfigEndpoint, [CmdEnterBootloader]),

        _ => throw new UnreachableException($"Unhandled Command variant: {command?.GetType().Name}")
    };

    /// <summary>
    /// Returns the number of bytes expected in the peripheral response for
    /// <paramref name="command"/>. Zero for commands that have no response (or whose
    /// response is variable-length and read by the shell, e.g. a 1-Wire scan).
    /// </summary>
    public static int ResponseLength(Command command) => command switch
    {
        Command.I2cTransaction(_, _, var readLen)       => 1 + readLen,            // 1 status + data
        Command.SpiTransaction(var tx, _, _, _, _, var burst, _)
            => burst == SpiBurstTx ? 0 : tx.Length,                               // BurstTx returns nothing
        Command.UartTransmit                            => 1,                      // ack byte
        Command.UartReceive                             => 33,                     // 32 data + 1 count at [32]
        Command.OneWireReset                            => 1,                      // presence byte
        _                                               => 0
    };

    // ── Decode ─────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a raw 41-byte pin-state buffer from the pin-report endpoint into an
    /// immutable <see cref="BoardReport"/>. Pure: no board state, no IO.
    /// </summary>
    /// <param name="raw">The raw packet bytes (must be ≥ <see cref="PinReportLength"/>).</param>
    /// <param name="sequence">Monotonically increasing counter assigned by the caller.</param>
    public static BoardReport DecodeReport(ReadOnlySpan<byte> raw, long sequence)
    {
        if (raw.Length < PinReportLength)
            throw new ArgumentException(
                $"Pin-state report must be {PinReportLength} bytes; got {raw.Length}.", nameof(raw));

        var builder = ImmutableArray.CreateBuilder<PinSnapshot>(PinCount);
        for (int i = 0; i < PinCount; i++)
        {
            byte high = raw[1 + i * 2];
            byte low  = raw[2 + i * 2];
            // The two bytes are mode-dependent: for a digital input the firmware
            // sets the high byte to the logic level (matching the original SDK's
            // `_digitalValue = highByte > 0`); for an analog input the pair is the
            // 12-bit sample (high << 8 | low). We decode both projections; the
            // consumer reads the one matching the pin's configured mode.
            builder.Add(new PinSnapshot(Digital: high != 0, Adc: (high << 8) | low));
        }
        return new BoardReport(sequence, builder.MoveToImmutable());
    }

    /// <summary>
    /// Decodes one 9-byte 1-Wire ROM-search response packet (status byte + 8 ROM
    /// bytes) into a 64-bit ROM address. The caller checks for the
    /// <see cref="OneWireScanTerminator"/> before calling this. Pure.
    /// </summary>
    public static ulong DecodeOneWireRom(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < OneWireRomPacketLength)
            throw new ArgumentException(
                $"1-Wire ROM packet must be {OneWireRomPacketLength} bytes; got {packet.Length}.", nameof(packet));

        // Faithful to the original SDK: reverse the whole 9-byte packet, then read the
        // first 8 bytes as a little-endian ulong.
        Span<byte> buf = stackalloc byte[OneWireRomPacketLength];
        packet[..OneWireRomPacketLength].CopyTo(buf);
        buf.Reverse();
        return BinaryPrimitives.ReadUInt64LittleEndian(buf[..8]);
    }

    // ── Plan ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the minimum set of <see cref="Command"/>s to transition the board
    /// from <paramref name="applied"/> to <paramref name="desired"/>. Pure: no IO,
    /// no state mutation, same inputs → same output. (ADR-0052 DEC-003.)
    /// </summary>
    /// <param name="desired">What the host wants the board to be configured as.</param>
    /// <param name="applied">
    /// The last successfully committed config, or <see langword="null"/> if the
    /// board has never been initialised. A <see langword="null"/> baseline prepends
    /// <see cref="Command.ConfigureDevice"/> and diffs against
    /// <see cref="BoardConfig.Blank"/>, so the whole <paramref name="desired"/>
    /// config is (re)applied from scratch — the reconnect path.
    /// </param>
    public static IReadOnlyList<Command> Plan(BoardConfig desired, BoardConfig? applied)
    {
        var cmds = new List<Command>();

        // ── Reconnect / first-init ─────────────────────────────────────
        if (applied is null)
            cmds.Add(new Command.ConfigureDevice());

        // Use Blank as the baseline when computing diffs on a null-applied board.
        applied ??= BoardConfig.Blank;

        // ── LED ────────────────────────────────────────────────────────
        if (desired.LedOn != applied.LedOn)
            cmds.Add(new Command.SetLed(desired.LedOn));

        // ── Per-pin: desired has a config, or had one that is now absent ─
        // Pins newly in desired / changed
        foreach (var (pin, desPin) in desired.Pins)
        {
            var appPin = applied.Pins.GetValueOrDefault(pin);

            bool modeChanged = desPin.Mode != (appPin?.Mode ?? PinMode.Reserved);
            // The ADC reference rides in the analog pin-config packet, so a reference
            // change on a still-analog pin must re-send ConfigurePin too.
            bool refChanged = desPin.Mode == PinMode.AnalogInput &&
                desPin.Reference != (appPin?.Reference ?? AdcReferenceLevel.Vref_3V3);

            if (modeChanged || refChanged)
                cmds.Add(new Command.ConfigurePin(pin, desPin.Mode, desPin.Reference));

            // Digital output value: only emit WriteDigital when the mode is already
            // push-pull and the value changed (ConfigurePin does not set the value).
            if (desPin.Mode == PinMode.PushPullOutput &&
                desPin.DigitalValue != (appPin?.DigitalValue ?? false))
            {
                cmds.Add(new Command.WriteDigital(pin, desPin.DigitalValue));
            }
        }

        // Pins that were configured but are now absent → reset to high-impedance
        foreach (var pin in applied.Pins.Keys)
        {
            if (!desired.Pins.ContainsKey(pin))
                cmds.Add(new Command.ConfigurePin(pin, PinMode.DigitalInput));
        }

        // ── Peripheral changes ─────────────────────────────────────────
        if (desired.I2c != applied.I2c)
        {
            cmds.Add(desired.I2c is { } i2c
                ? new Command.ConfigureI2c(true, i2c.SpeedKhz)
                : new Command.ConfigureI2c(false));
        }

        if (desired.Spi != applied.Spi)
            cmds.Add(new Command.ConfigureSpi(desired.Spi is not null));

        if (desired.Uart != applied.Uart)
        {
            cmds.Add(desired.Uart is { } uart
                ? new Command.ConfigureUart(true, uart.Baud, uart.OpenDrainTx, uart.Mode)
                : new Command.ConfigureUart(false));
        }

        if (desired.Pwm != applied.Pwm)
        {
            cmds.Add(desired.Pwm is { } pwm
                ? new Command.ConfigurePwm(pwm.EnableMode, pwm.Frequency, pwm.Duty7, pwm.Duty8, pwm.Duty9)
                : new Command.ConfigurePwm(0, PwmFrequency.Freq732Hz, 0, 0, 0));
        }

        // ── Soft-PWM (aggregate) ───────────────────────────────────────
        if (!SoftPwmEquals(desired.SoftPwm, applied.SoftPwm))
        {
            // Newly soft-PWM pins must first be driven as push-pull outputs.
            foreach (var pin in desired.SoftPwm.Keys)
                if (!applied.SoftPwm.ContainsKey(pin))
                    cmds.Add(new Command.ConfigurePin(pin, PinMode.PushPullOutput));

            // Pins no longer soft-PWM return to high-impedance inputs — unless the pin
            // was simultaneously reassigned to an explicit mode (handled above), in
            // which case releasing it here would clobber that reassignment.
            foreach (var pin in applied.SoftPwm.Keys)
                if (!desired.SoftPwm.ContainsKey(pin) && !desired.Pins.ContainsKey(pin))
                    cmds.Add(new Command.ConfigurePin(pin, PinMode.DigitalInput));

            // One aggregate packet describes the full set (empty → disable).
            cmds.Add(new Command.ConfigureSoftPwm(desired.SoftPwm));
        }

        // ── Parallel interface ─────────────────────────────────────────
        if (!ParallelEquals(desired.Parallel, applied.Parallel))
        {
            cmds.Add(desired.Parallel is { } p
                ? new Command.ConfigureParallel(
                    true, p.DelayMicroseconds, p.DataBusPins, p.RegisterSelectPin, p.ReadWritePin, p.EnablePin)
                : new Command.ConfigureParallel(false, 0, ImmutableArray<byte>.Empty, -1, -1, -1));
        }

        return cmds;
    }

    private static bool SoftPwmEquals(
        ImmutableDictionary<byte, ushort> a, ImmutableDictionary<byte, ushort> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (pin, ticks) in a)
            if (!b.TryGetValue(pin, out var other) || other != ticks)
                return false;
        return true;
    }

    private static bool ParallelEquals(ParallelConfig? a, ParallelConfig? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.RegisterSelectPin == b.RegisterSelectPin
            && a.ReadWritePin == b.ReadWritePin
            && a.EnablePin == b.EnablePin
            && a.DelayMicroseconds == b.DelayMicroseconds
            && a.DataBusPins.AsSpan().SequenceEqual(b.DataBusPins.AsSpan());
    }

    // ── Encoding helpers (pure) ────────────────────────────────────────

    private static byte[] PinModeBytes(byte pin, PinMode mode, AdcReferenceLevel reference)
    {
        // PinMode enum values align with the firmware's pin-config command bytes:
        // DigitalInput=1, PushPullOutput=2, OpenDrainOutput=3, AnalogInput=4.
        byte cmd = mode switch
        {
            PinMode.DigitalInput    => PinCmdDigitalInput,
            PinMode.PushPullOutput  => PinCmdPushPullOutput,
            PinMode.OpenDrainOutput => PinCmdOpenDrainOutput,
            PinMode.AnalogInput     => PinCmdAnalogInput,
            _                       => PinCmdDigitalInput, // Reserved / unknown → release
        };
        // The byte after the mode carries the ADC reference for analog inputs; it is
        // zero (the firmware ignores it) for every other mode.
        byte arg = mode == PinMode.AnalogInput ? (byte)reference : (byte)0;
        return [pin, cmd, arg, 0, 0, 0];
    }

    /// <summary>I²C rate byte: <c>round(256 − 4000 / (3·kHz))</c>.</summary>
    internal static byte I2cRateByte(int speedKhz)
    {
        double th0 = 256.0 - 4000.0 / (3.0 * speedKhz);
        if (th0 is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(speedKhz),
                "I²C speed out of range (≈62.5 kHz to 16 MHz).");
        return (byte)Math.Round(th0);
    }

    private static byte[] I2cTransactionBytes(byte address, ReadOnlySpan<byte> tx, int readLen)
    {
        var packet = new byte[4 + tx.Length];
        packet[0] = CmdI2cTransaction;
        packet[1] = address;
        packet[2] = (byte)tx.Length;
        packet[3] = (byte)readLen;
        tx.CopyTo(packet.AsSpan(4));
        return packet;
    }

    /// <summary>
    /// SPI clock divisor byte: <c>clamp(round(24 / MHz − 1), 0, 255)</c>, after applying
    /// the firmware's silicon-bug guard.
    /// </summary>
    /// <remarks>
    /// The EFM8's SPI FIFO has a silicon bug that can lock the peripheral up under heavy
    /// USB traffic when it is clocked between 0.8 MHz and 6 MHz, so the original SDK
    /// disallows that band — <c>Treehopper.HardwareSpi.SendReceiveAsync</c> silently
    /// rounds any request in (0.8, 6) MHz up to 6. We replicate that here verbatim, since
    /// every SPI transfer flows through this one method: a caller asking for, say, 4 MHz
    /// is clocked at the safe 6 MHz instead of wedging the board. (A lock-up in that band
    /// freezes the firmware's single-threaded polled SPI loop, which stops draining the
    /// peripheral OUT endpoint, blocks the host's next write forever, and "bricks" the
    /// board until it is physically replugged.) Use ≤ 0.8 MHz for genuinely slow clocking.
    /// </remarks>
    internal static byte SpiClockByte(double clockMhz, bool allowDangerBand = false)
    {
        // Silicon-bug guard (ADR-0039 firmware-compat constraint): the SPI FIFO can lock
        // up under heavy USB traffic at 0.8–6 MHz. Round that band up to the safe 6 MHz
        // boundary, exactly as the original Treehopper.HardwareSpi.SendReceiveAsync does.
        //
        // DEBUG-ONLY bypass: when allowDangerBand is true the requested speed is clocked
        // verbatim (lock-up reproduction over C2). The decision is made in the imperative
        // shell (TreehopperBoard reads TREEHOPPER_SPI_DANGER_BAND once and sets the flag on
        // the command) so this codec stays a pure, deterministic, environment-free function
        // — ADR-0052 DEC-001. Never enable in production: it deliberately re-enables the
        // exact condition this guard exists to prevent.
        if (!allowDangerBand && clockMhz is > 0.8 and < 6.0)
            clockMhz = 6.0;
        return (byte)Math.Clamp((int)Math.Round(24.0 / clockMhz - 1), 0, 255);
    }

    private static byte[] SpiTransactionBytes(
        ReadOnlySpan<byte> tx, int csPin, byte csMode, double mhz, SpiMode mode, byte burst,
        bool allowDangerBand)
    {
        // Receive-only bursts ship just the 7-byte header; the firmware clocks in
        // `len` bytes without host-supplied MOSI data. Every other mode appends the
        // MOSI payload after the header.
        bool headerOnly = burst == SpiBurstRx;
        var packet = new byte[7 + (headerOnly ? 0 : tx.Length)];
        packet[0] = CmdSpiTransaction;
        packet[1] = (byte)(csPin is < 0 or > 255 ? 0xFF : csPin);
        packet[2] = csMode;
        packet[3] = SpiClockByte(mhz, allowDangerBand);
        packet[4] = (byte)mode;
        packet[5] = burst;
        packet[6] = (byte)tx.Length;
        if (!headerOnly)
            tx.CopyTo(packet.AsSpan(7));
        return packet;
    }

    private static byte[] UartConfigBytes(bool enable, int baud, bool openDrainTx, UartMode mode)
    {
        if (!enable)
            return [CmdUartConfig, UartConfigDisabled];

        if (mode == UartMode.OneWire)
            return [CmdUartConfig, UartConfigOneWire];

        var (timer, prescaler) = UartTimer(baud);
        return [CmdUartConfig, UartConfigStandard, timer, (byte)(prescaler ? 1 : 0), (byte)(openDrainTx ? 1 : 0)];
    }

    /// <summary>
    /// Chooses the 8-bit timer reload + prescaler flag for a UART baud rate.
    /// Uses the 2 MHz prescaled clock or the 24 MHz base clock, whichever gives
    /// lower baud error.
    /// </summary>
    internal static (byte Timer, bool UsePrescaler) UartTimer(int baud)
    {
        int withPre = (int)Math.Round(256.0 - 2_000_000.0 / baud);
        int noPre   = (int)Math.Round(256.0 - 24_000_000.0 / baud);
        bool preOob   = withPre is < 0 or > 255;
        bool noPreOob = noPre   is < 0 or > 255;

        if (preOob && noPreOob)
            throw new ArgumentOutOfRangeException(nameof(baud),
                "Baud rate out of range (≈7813 to 2 400 000).");
        if (preOob)   return ((byte)noPre, false);
        if (noPreOob) return ((byte)withPre, true);

        double preErr   = Math.Abs(baud - 2_000_000.0 / (256 - withPre));
        double noPreErr = Math.Abs(baud - 24_000_000.0 / (256 - noPre));
        return preErr > noPreErr ? ((byte)noPre, false) : ((byte)withPre, true);
    }

    private static byte[] UartTransmitBytes(ReadOnlySpan<byte> data)
    {
        var packet = new byte[3 + data.Length];
        packet[0] = CmdUartTransaction;
        packet[1] = UartCmdTransmit;
        packet[2] = (byte)data.Length;
        data.CopyTo(packet.AsSpan(3));
        return packet;
    }

    /// <summary>Converts a 0.0–1.0 duty cycle to the 16-bit register value.</summary>
    internal static ushort PwmDutyRegister(double dutyCycle)
        => (ushort)Math.Round(Math.Clamp(dutyCycle, 0.0, 1.0) * ushort.MaxValue);

    private static byte[] PwmConfigBytes(byte enableMode, PwmFrequency frequency,
        double duty7, double duty8, double duty9)
    {
        var packet = new byte[9];
        packet[0] = CmdPwmConfig;
        packet[1] = enableMode;
        packet[2] = (byte)frequency;
        WriteDuty(packet, 3, duty7);
        WriteDuty(packet, 5, duty8);
        WriteDuty(packet, 7, duty9);
        return packet;

        static void WriteDuty(byte[] p, int offset, double duty)
        {
            ushort reg = PwmDutyRegister(duty);
            p[offset]     = (byte)(reg & 0xFF);
            p[offset + 1] = (byte)(reg >> 8);
        }
    }

    /// <summary>Soft-PWM tick value from a 0.0–1.0 duty cycle (round to nearest).</summary>
    internal static ushort SoftPwmTicksFromDuty(double dutyCycle)
        => (ushort)Math.Round(Math.Clamp(dutyCycle, 0.0, 1.0) * ushort.MaxValue);

    /// <summary>
    /// Soft-PWM tick value from a pulse width in microseconds (0.25 µs / tick),
    /// clamped to the 16-bit range (≈0–16383.75 µs).
    /// </summary>
    internal static ushort SoftPwmTicksFromPulseWidth(double microseconds)
        => (ushort)Math.Clamp(Math.Round(microseconds / 0.25), 0, ushort.MaxValue);

    /// <summary>
    /// Encodes the aggregate soft-PWM config: a delta-timing schedule across every
    /// active pin, sorted by tick value. Faithful to the original SoftPwmManager.
    /// An empty set disables soft-PWM.
    /// </summary>
    internal static byte[] SoftPwmBytes(ImmutableDictionary<byte, ushort> pins)
    {
        if (pins.Count == 0)
            return [CmdSoftPwmConfig, 0];

        var list = pins.OrderBy(kv => kv.Value).ToList();
        int count = list.Count + 1;                 // an extra "wrap to period end" entry
        var config = new byte[2 + 3 * count];
        config[0] = CmdSoftPwmConfig;
        config[1] = (byte)count;

        int i = 2;
        int time = 0;
        for (int j = 0; j < count; j++)
        {
            int ticks = j < list.Count ? list[j].Value - time : ushort.MaxValue - time;
            int tmrVal = ushort.MaxValue - ticks;

            config[i++] = j == 0 ? (byte)0 : list[j - 1].Key;
            config[i++] = (byte)(tmrVal >> 8);
            config[i++] = (byte)(tmrVal & 0xFF);
            time += ticks;
        }
        return config;
    }

    private static byte[] ParallelConfigBytes(
        bool enable, int delayMicroseconds, ImmutableArray<byte> dataBusPins,
        int registerSelectPin, int readWritePin, int enablePin)
    {
        int busCount = dataBusPins.Length;
        var cmd = new byte[7 + busCount];
        cmd[0] = CmdParallelConfig;
        cmd[1] = (byte)(enable ? 1 : 0);
        cmd[2] = (byte)delayMicroseconds;
        cmd[3] = (byte)busCount;
        cmd[4] = (byte)(registerSelectPin < 0 ? 0xFF : registerSelectPin);
        cmd[5] = (byte)(readWritePin < 0 ? 0xFF : readWritePin);
        cmd[6] = (byte)(enablePin < 0 ? 0xFF : enablePin);
        for (int i = 0; i < busCount; i++)
            cmd[7 + i] = dataBusPins[i];
        return cmd;
    }

    private static byte[] ParallelWriteBytes(bool isData, ImmutableArray<uint> words, int busWidth)
    {
        int wordCount = words.Length;
        byte[] cmd;
        if (busWidth <= 8)
        {
            cmd = new byte[wordCount + 3];
            for (int i = 0; i < wordCount; i++)
                cmd[3 + i] = (byte)words[i];
        }
        else
        {
            cmd = new byte[wordCount * 2 + 3];
            for (int i = 0; i < wordCount; i++)
            {
                cmd[3 + i * 2]     = (byte)(words[i] >> 8);
                cmd[3 + i * 2 + 1] = (byte)(words[i] & 0xFF);
            }
        }
        cmd[0] = CmdParallelTransaction;
        cmd[1] = isData ? ParallelCmdWriteData : ParallelCmdWriteCommand;
        cmd[2] = (byte)wordCount;
        return cmd;
    }

    /// <summary>
    /// Frames a name / serial-number EEPROM write: command byte, character count, then
    /// the UTF-8 bytes. Faithful to the original SDK (the count is the string length).
    /// </summary>
    private static byte[] IdentityBytes(byte command, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var packet = new byte[bytes.Length + 2];
        packet[0] = command;
        packet[1] = (byte)text.Length;
        bytes.CopyTo(packet, 2);
        return packet;
    }
}
