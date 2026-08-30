// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// The 6-byte DFU_GETSTATUS response, decoded: <c>bStatus</c>, <c>bwPollTimeout</c>
/// (3 bytes, little-endian milliseconds), <c>bState</c>, <c>iString</c>. Pure value.
/// </summary>
/// <remarks>
/// <see cref="PollTimeout"/> is the device telling the host how long to wait before the
/// next GETSTATUS while it executes a command — the shell owns that wait (ADR-0052 DEC-004).
/// </remarks>
public readonly record struct DfuStatus(DfuStatusCode Status, TimeSpan PollTimeout, DfuState State, byte StringIndex)
{
    /// <summary>Decodes the 6-byte GETSTATUS payload.</summary>
    public static DfuStatus Decode(ReadOnlySpan<byte> response)
    {
        if (response.Length < 6)
            throw new ArgumentException($"A DFU status response must be 6 bytes (got {response.Length}).", nameof(response));

        int pollMs = response[1] | (response[2] << 8) | (response[3] << 16);
        return new DfuStatus(
            (DfuStatusCode)response[0],
            TimeSpan.FromMilliseconds(pollMs),
            (DfuState)response[4],
            response[5]);
    }
}
