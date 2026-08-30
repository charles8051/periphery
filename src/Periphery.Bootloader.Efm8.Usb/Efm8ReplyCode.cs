// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The one-byte status the EFM8 factory bootloader returns in the first byte of
/// its input report after each boot-record frame.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="Acknowledge"/> (<c>'@'</c> = <c>0x40</c>) means "frame accepted,
/// continue". Every other value aborts the upload. This is the load-bearing
/// invariant — the uploader stops on the first non-<see cref="Acknowledge"/> reply.
/// </para>
/// <para>
/// The exact meaning of the three error bytes (<c>'A' 'B' 'C'</c>) varies between
/// the AN945 primary source and the open re-implementations; the labels here follow
/// the host-side convention in the SiLabs <c>efm8load.py</c> reference
/// (<c>efm8load.py:112</c>, valid replies <c>b'@ABC'</c>) and
/// the survey at <c>docs/explorations/treehopper-firmware-update.md</c>. They are
/// diagnostic only — the uploader treats every non-<c>'@'</c> byte identically (stop
/// and report), so a wrong label can never cause a wrong continue/stop decision.
/// </para>
/// </remarks>
public enum Efm8ReplyCode : byte
{
    /// <summary><c>'@'</c> (0x40) — frame accepted. The only value that continues the upload.</summary>
    Acknowledge = 0x40,

    /// <summary><c>'A'</c> (0x41) — range error (address outside a writable region).</summary>
    RangeError = 0x41,

    /// <summary><c>'B'</c> (0x42) — CRC / verify error.</summary>
    CrcError = 0x42,

    /// <summary><c>'C'</c> (0x43) — other bootloader error.</summary>
    OtherError = 0x43,

    /// <summary>
    /// <c>'?'</c> (0x3F) — any byte the bootloader never emits, or a timeout /
    /// empty read. Mirrors <c>efm8load.py</c>'s mapping of an unrecognised reply
    /// to <c>'?'</c> (<c>efm8load.py:115</c>).
    /// </summary>
    Unknown = 0x3F,
}
