// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Control;

/// <summary>
/// The app's view of one pin: its host-believed <see cref="Mode"/> (the protocol has no
/// mode read-back, so this is tracked from the modes we set) plus the last-reported
/// live values from the board's <c>BoardReport</c> stream.
/// </summary>
/// <param name="Number">Pin index, 0–19.</param>
/// <param name="Mode">Host-believed electrical mode.</param>
/// <param name="High">Last-reported logic level.</param>
/// <param name="Adc">Last-reported raw 12-bit ADC sample (meaningful only for analog-input pins).</param>
public sealed record PinView(int Number, PinMode Mode, bool High, int Adc);
