// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Treehopper.Wire;

/// <summary>
/// Closed union of all commands that can be sent to a Treehopper board.
/// Every variant is an immutable value — no IO, no clock, no
/// <see cref="System.Threading.Tasks.Task"/>. Encoded to wire bytes by
/// <see cref="TreehopperWire.Encode"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>private protected</c> base constructor prevents external extension:
/// only the nested sealed records defined here can derive from
/// <see cref="Command"/>. <see cref="TreehopperWire.Encode"/> is therefore
/// exhaustive — every variant is handled and the compiler enforces completeness.
/// (ADR-0052 DEC-001.)
/// </para>
/// <para>
/// Internal: this is the wire-codec's input alphabet, not user-facing API. The
/// board's public surface (LED, leases, pin handles, <c>ReconcileAsync</c>) builds
/// these internally; tests reach them via <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
internal abstract record Command
{
    private protected Command() { }

    // ── Pin-config endpoint (0x01) ────────────────────────────────────

    /// <summary>
    /// Sets a pin's electrical mode (high-impedance → output → analog…). For
    /// <see cref="PinMode.AnalogInput"/> the <paramref name="Reference"/> selects the
    /// ADC reference; it is ignored for every other mode.
    /// </summary>
    public sealed record ConfigurePin(
        byte Pin, PinMode Mode, AdcReferenceLevel Reference = AdcReferenceLevel.Vref_3V3) : Command;

    /// <summary>Drives a push-pull output pin high or low.</summary>
    public sealed record WriteDigital(byte Pin, bool High) : Command;

    // ── Peripheral-config endpoint (0x02) ─────────────────────────────

    /// <summary>Initialises the board (sent once on open / reconnect).</summary>
    public sealed record ConfigureDevice() : Command;

    /// <summary>Turns the on-board LED on or off.</summary>
    public sealed record SetLed(bool On) : Command;

    /// <summary>Enables or disables the I²C module.</summary>
    public sealed record ConfigureI2c(bool Enable, int SpeedKhz = 100) : Command;

    /// <summary>Runs an I²C read/write transaction. Triggers a peripheral response.</summary>
    public sealed record I2cTransaction(byte Address, ReadOnlyMemory<byte> Tx, int ReadLen) : Command;

    /// <summary>Enables or disables the SPI module.</summary>
    public sealed record ConfigureSpi(bool Enable) : Command;

    /// <summary>
    /// Runs a full-duplex SPI transfer. Triggers a peripheral response (MISO bytes)
    /// unless <see cref="Burst"/> is transmit-only. Clock speed and mode are
    /// per-transaction since the firmware latches them each time.
    /// </summary>
    /// <param name="AllowDangerBand">
    /// When <see langword="false"/> (the default, and always in production) a
    /// requested clock in the EFM8's silicon-bug band (0.8–6 MHz) is rounded up to
    /// the safe 6 MHz boundary during encoding. When <see langword="true"/> the
    /// codec clocks the requested speed verbatim — a DEBUG-ONLY lock-up-reproduction
    /// path. The imperative shell (the board) decides this from the
    /// <c>TREEHOPPER_SPI_DANGER_BAND</c> environment variable, read once at board
    /// construction; the codec itself stays pure and reads no environment.
    /// </param>
    public sealed record SpiTransaction(
        ReadOnlyMemory<byte> Tx,
        int ChipSelectPin,
        byte ChipSelectMode,
        double SpeedMhz,
        SpiMode Mode,
        byte Burst,
        bool AllowDangerBand = false) : Command;

    /// <summary>Enables or disables the UART, in standard or 1-Wire mode.</summary>
    public sealed record ConfigureUart(
        bool Enable, int Baud = 9600, bool OpenDrainTx = false, UartMode Mode = UartMode.Uart) : Command;

    /// <summary>Sends bytes over the UART / 1-Wire bus. Triggers a peripheral response (ack).</summary>
    public sealed record UartTransmit(ReadOnlyMemory<byte> Data) : Command;

    /// <summary>
    /// Reads the UART receive buffer. Triggers a 33-byte peripheral response (32 data
    /// bytes + a count byte). In 1-Wire mode <see cref="OneWireBytes"/> &gt; 0 requests
    /// that many bytes to be clocked in first.
    /// </summary>
    public sealed record UartReceive(int OneWireBytes = 0) : Command;

    /// <summary>Issues a 1-Wire reset pulse. Triggers a 1-byte response (non-zero = device present).</summary>
    public sealed record OneWireReset() : Command;

    /// <summary>
    /// Starts a 1-Wire ROM search. The firmware streams 9-byte ROM packets terminated
    /// by a <c>0xFF</c> status byte; the shell loops the read rather than using a fixed
    /// response length.
    /// </summary>
    public sealed record OneWireScan() : Command;

    /// <summary>
    /// Configures hardware PWM: cumulative enable mode, shared frequency, and per-channel duty.
    /// A single packet carries the full state; re-sent on every duty or frequency change.
    /// </summary>
    public sealed record ConfigurePwm(
        byte EnableMode,
        PwmFrequency Frequency,
        double Duty7,
        double Duty8,
        double Duty9) : Command;

    /// <summary>
    /// Configures soft-PWM across every active soft-PWM pin in one aggregate packet
    /// (pin → 16-bit tick count). An empty map disables soft-PWM. The firmware drives
    /// the pins from one timer, so the host re-sends the whole set on any change.
    /// </summary>
    public sealed record ConfigureSoftPwm(ImmutableDictionary<byte, ushort> Pins) : Command;

    /// <summary>
    /// Configures the 8080-style parallel interface (data bus + RS/RW/E control pins).
    /// A pin value of -1 means "unused". An empty <see cref="DataBusPins"/> with
    /// <see cref="Enable"/> false disables the module.
    /// </summary>
    public sealed record ConfigureParallel(
        bool Enable,
        int DelayMicroseconds,
        ImmutableArray<byte> DataBusPins,
        int RegisterSelectPin,
        int ReadWritePin,
        int EnablePin) : Command;

    /// <summary>
    /// Writes one or more words to the parallel bus with RS asserted for data
    /// (<see cref="IsData"/> true) or command (false). Words are 1 byte each for a
    /// bus ≤ 8 pins, 2 bytes (big-endian) otherwise. No response.
    /// </summary>
    public sealed record ParallelWrite(bool IsData, ImmutableArray<uint> Words, int BusWidth) : Command;

    /// <summary>Writes a new device name to EEPROM (≤ 60 chars). Takes effect after a reboot.</summary>
    public sealed record UpdateName(string Name) : Command;

    /// <summary>Writes a new serial number to EEPROM (≤ 60 chars). Takes effect after a reboot.</summary>
    public sealed record UpdateSerial(string Serial) : Command;

    /// <summary>Reboots the board MCU (drops and re-enumerates the USB device).</summary>
    public sealed record Reboot() : Command;

    /// <summary>Reboots into the USB-HID bootloader (re-enumerates as a DFU device).</summary>
    public sealed record EnterBootloader() : Command;
}
