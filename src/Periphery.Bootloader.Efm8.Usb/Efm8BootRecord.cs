// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// One parsed boot-record frame from a hex2boot-produced <c>.efm8</c>/<c>.tfi</c>
/// stream: the start byte <c>'$'</c> (0x24), a one-byte length, the command byte,
/// and its payload. An immutable view over a slice of the source buffer — no copy.
/// </summary>
/// <remarks>
/// The frame is replayed to the device verbatim. This type never constructs or
/// mutates a frame; the device-bricking guarantees (reset-vector written last,
/// no Lock <c>0x35</c> record) live in <c>hex2boot</c> upstream, not here.
/// See <c>hex2boot.py:72-107</c> for the record encoders.
/// </remarks>
public readonly struct Efm8BootRecord
{
    /// <summary>The full on-wire frame: <c>0x24</c>, length, command, payload.</summary>
    public ReadOnlyMemory<byte> Frame { get; }

    /// <summary>Zero-based position of this record in the stream.</summary>
    public int Index { get; }

    internal Efm8BootRecord(int index, ReadOnlyMemory<byte> frame)
    {
        Index = index;
        Frame = frame;
    }

    /// <summary>
    /// The bootloader command byte (e.g. <c>0x31</c> Setup, <c>0x32</c> Erase,
    /// <c>0x33</c> Write, <c>0x34</c> Verify, <c>0x36</c> RunApp).
    /// </summary>
    public byte Command => Frame.Span[2];

    /// <summary>
    /// The declared length byte — the count of bytes following it (command + data).
    /// Equal to <c>Frame.Length - 2</c> for a well-formed record.
    /// </summary>
    public int DeclaredLength => Frame.Span[1];
}
