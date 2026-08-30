// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Globalization;

namespace Periphery.Hid.Codecs;

/// <summary>
/// Pure parser for the status line shared by every dialect in the Megatec Qx
/// family (Megatec <c>Q1</c>, Voltronic <c>QS</c>, …). The verb that elicits
/// the line differs per dialect (see <see cref="MegatecDialect"/>); the line's
/// <em>shape</em> does not — so parsing is dialect-agnostic and lives here as a
/// total function.
/// </summary>
/// <remarks>
/// <para>
/// <b>Functional core.</b> No I/O, no device handle, no clock: the same input
/// string always yields the same <see cref="HidBatterySnapshot"/>. The
/// imperative shell (<see cref="MegatecQxCodec"/> over <see cref="MegatecWire"/>)
/// owns the transport and the dialect handshake; this type only interprets bytes
/// that have already arrived.
/// </para>
/// <para>
/// Status-line shape (leading <c>'('</c> included, space-separated, trailing
/// <c>'\r'</c> stripped by the wire layer):
/// </para>
/// <code>
/// (MMM.M NNN.N PPP.P QQQ RR.R SS.S TT.T b7b6b5b4b3b2b1b0
///    │    │    │    │   │    │   │   │
///    │    │    │    │   │    │   │   └── Status bits (MSB→LSB)
///    │    │    │    │   │    │   └────── Temperature (°C, "--.-" if no sensor)
///    │    │    │    │   │    └────────── Battery voltage (V)
///    │    │    │    │   └─────────────── Input frequency (Hz)
///    │    │    │    └─────────────────── Output load (%)
///    │    │    └──────────────────────── Output voltage (V)
///    │    └───────────────────────────── Input fault voltage (V)
///    └────────────────────────────────── Input voltage (V)
/// </code>
/// <para>
/// Status bits (8-char ASCII binary, MSB→LSB):
/// </para>
/// <list type="bullet">
/// <item><b>b7 = utility fail</b> — 1 means running on battery.</item>
/// <item><b>b6 = battery low</b> — 1 means imminent shutdown.</item>
/// <item><b>b5 = bypass / boost active</b></item>
/// <item><b>b4 = UPS failed</b></item>
/// <item><b>b3 = UPS type</b> — 0 = on-line, 1 = standby/off-line topology.</item>
/// <item><b>b2 = test in progress</b></item>
/// <item><b>b1 = shutdown active</b></item>
/// <item><b>b0 = beeper on</b></item>
/// </list>
/// </remarks>
internal static class MegatecStatus
{
    /// <summary>
    /// Battery voltage thresholds for single 12V cell estimation. Outside this
    /// range the codec reports a <c>null</c> charge percent rather than guess —
    /// multi-cell UPSs (24V, 48V packs) would otherwise produce wildly wrong
    /// numbers. A future revision can read the <c>F</c> (rating) line to learn
    /// the nominal voltage and pick the right curve.
    /// </summary>
    private const double SingleCellMinVolts = 9.0;
    private const double SingleCellMaxVolts = 15.0;
    private const double SingleCellEmptyVolts = 10.5;
    private const double SingleCellFullVolts = 13.6;

    /// <summary>
    /// Parses a Megatec-family status line (prefix-inclusive <c>'('</c>,
    /// terminator-exclusive). Throws <see cref="HidTransferException"/> on a
    /// malformed line.
    /// </summary>
    public static HidBatterySnapshot Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!TryParse(response, out var snapshot, out var error))
            throw new HidTransferException(
                $"Megatec status response {error}: '{response}'.",
                new FormatException(error));

        return snapshot;
    }

    /// <summary>
    /// Non-throwing well-formedness check, used by claim-and-bind detection to
    /// decide whether a probe actually answered (vs. returned noise from the
    /// multicast input stream). Returns <c>true</c> exactly when
    /// <see cref="Parse"/> would succeed.
    /// </summary>
    public static bool IsWellFormed(string? response)
        => response is not null && TryParse(response, out _, out _);

    /// <summary>
    /// Shared validation + decode. Returns <c>false</c> with a human-readable
    /// <paramref name="error"/> fragment instead of throwing, so detection can
    /// probe without paying exception cost and <see cref="Parse"/> can throw a
    /// single consistent message.
    /// </summary>
    private static bool TryParse(string response, out HidBatterySnapshot snapshot, out string? error)
    {
        snapshot = default;

        // Expected: "(MMM.M NNN.N PPP.P QQQ RR.R SS.S TT.T b7b6b5b4b3b2b1b0"
        if (response.Length < 2 || response[0] != '(')
        {
            error = "did not start with '('";
            return false;
        }

        var fields = response[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 8)
        {
            error = $"has {fields.Length} field(s), expected at least 8";
            return false;
        }

        // Fields we care about:
        //   [5] battery voltage
        //   [7] status bits
        // (Input voltage, load, frequency, temperature are all interesting but
        // don't fit DeviceInfo's current battery fields. A future codec
        // extension can promote them via a Properties bag.)
        var statusBits = fields[7];
        if (statusBits.Length < 8)
        {
            error = $"status-bits field is {statusBits.Length} char(s), expected 8";
            return false;
        }

        bool utilityFail = statusBits[0] == '1';
        bool batteryLow = statusBits[1] == '1';
        bool externalPower = !utilityFail;

        // BatteryStatus is flow direction only — utility-fail dictates
        // Discharging vs NotCharging. The orthogonal "battery low" signal
        // travels alongside on IsBatteryLow (see HidBatterySnapshot and
        // DeviceInfo.IsBatteryLow); collapsing it into the status enum would
        // force a choice between "Discharging" and a hypothetical "Critical"
        // value and lose one of the two facts. The dialect (Q1 vs QS vs …)
        // doesn't expose a distinct "charging vs float" signal, so on line
        // power we report NotCharging regardless of charge level.
        BatteryStatus batteryStatus = utilityFail
            ? BatteryStatus.Discharging
            : BatteryStatus.NotCharging;

        int? chargePercent = EstimateChargePercent(fields[5]);

        snapshot = new HidBatterySnapshot(
            BatteryChargePercent: chargePercent,
            BatteryStatus: batteryStatus,
            IsExternalPowerConnected: externalPower,
            IsBatteryLow: batteryLow);
        error = null;
        return true;
    }

    /// <summary>
    /// Rough percent estimate from battery voltage assuming a single 12V cell.
    /// Returns <c>null</c> when the voltage falls outside the single-cell range
    /// (multi-cell UPSs need <c>F</c>-rating context to interpret correctly,
    /// deferred to a future codec revision).
    /// </summary>
    private static int? EstimateChargePercent(string field)
    {
        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double volts))
            return null;

        if (volts < SingleCellMinVolts || volts > SingleCellMaxVolts)
            return null;

        double pct = (volts - SingleCellEmptyVolts) / (SingleCellFullVolts - SingleCellEmptyVolts) * 100.0;
        return (int)Math.Clamp(Math.Round(pct), 0, 100);
    }
}
