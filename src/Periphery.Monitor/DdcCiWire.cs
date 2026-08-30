// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Monitor;

/// <summary>
/// Pure DDC/CI (MCCS over DDC2Bi) frame codec — the protocol layer the Linux
/// I2C backend speaks. Encodes host→display command frames and decodes
/// display→host replies, including the virtual-address XOR checksums.
/// </summary>
/// <remarks>
/// Framing recap: the display is I2C slave 0x37. Host frames are written as
/// <c>[0x51, len|0x80, payload…, chk]</c> where the checksum XOR includes the
/// display's write address byte 0x6E (0x37&lt;&lt;1) that the I2C layer
/// transmits implicitly. Reply frames read back as
/// <c>[0x6E, len|0x80, payload…, chk]</c> with the checksum seeded by the
/// host's virtual reply address 0x50. Pure functions, golden-vector tested;
/// the backend owns fds, ioctls, and the mandatory inter-command delays.
/// </remarks>
internal static class DdcCiWire
{
    /// <summary>The display's 8-bit write address, included in host-frame checksums.</summary>
    internal const byte DisplayWriteAddress = 0x6E;

    /// <summary>The host's virtual address, seeding reply checksums.</summary>
    internal const byte HostReplyAddress = 0x50;

    /// <summary>The host "source address" byte that leads every host frame.</summary>
    internal const byte HostSourceAddress = 0x51;

    /// <summary>Minimum quiet time between DDC/CI commands (MCCS mandates 40 ms; ddcutil defaults higher).</summary>
    internal static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(50);

    /// <summary>Delay between writing a request and reading its reply.</summary>
    internal static readonly TimeSpan ReplyDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>Get VCP Feature request (opcode 0x01): 5 bytes.</summary>
    internal static byte[] EncodeGetVcp(byte vcpCode)
    {
        var frame = new byte[] { HostSourceAddress, 0x82, 0x01, vcpCode, 0 };
        frame[^1] = Checksum(DisplayWriteAddress, frame.AsSpan(..^1));
        return frame;
    }

    /// <summary>Set VCP Feature request (opcode 0x03): 7 bytes.</summary>
    internal static byte[] EncodeSetVcp(byte vcpCode, ushort value)
    {
        var frame = new byte[]
        {
            HostSourceAddress, 0x84, 0x03, vcpCode,
            (byte)(value >> 8), (byte)(value & 0xFF), 0,
        };
        frame[^1] = Checksum(DisplayWriteAddress, frame.AsSpan(..^1));
        return frame;
    }

    /// <summary>Capabilities request (opcode 0xF3) for one fragment at <paramref name="offset"/>.</summary>
    internal static byte[] EncodeCapabilitiesRequest(ushort offset)
    {
        var frame = new byte[]
        {
            HostSourceAddress, 0x83, 0xF3,
            (byte)(offset >> 8), (byte)(offset & 0xFF), 0,
        };
        frame[^1] = Checksum(DisplayWriteAddress, frame.AsSpan(..^1));
        return frame;
    }

    /// <summary>The fixed length of a Get VCP Feature reply.</summary>
    internal const int GetVcpReplyLength = 11;

    /// <summary>
    /// Decodes a Get VCP Feature reply:
    /// <c>[0x6E, 0x88, 0x02, rc, code, type, maxHi, maxLo, curHi, curLo, chk]</c>.
    /// </summary>
    internal static bool TryDecodeGetVcpReply(
        ReadOnlySpan<byte> reply, byte expectedCode, out VcpFeatureValue value, out string? error)
    {
        value = default;

        if (reply.Length < GetVcpReplyLength)
        {
            error = $"reply too short ({reply.Length} bytes)";
            return false;
        }

        reply = reply[..GetVcpReplyLength];
        if (reply[0] != DisplayWriteAddress)
        {
            error = $"unexpected source address 0x{reply[0]:X2}";
            return false;
        }
        if (reply[1] != 0x88)
        {
            error = $"unexpected length byte 0x{reply[1]:X2}";
            return false;
        }
        if (Checksum(HostReplyAddress, reply[..^1]) != reply[^1])
        {
            error = "checksum mismatch";
            return false;
        }
        if (reply[2] != 0x02)
        {
            error = $"unexpected opcode 0x{reply[2]:X2}";
            return false;
        }
        if (reply[3] != 0x00)
        {
            error = reply[3] == 0x01
                ? "monitor reports the VCP code as unsupported"
                : $"monitor returned result code 0x{reply[3]:X2}";
            return false;
        }
        if (reply[4] != expectedCode)
        {
            error = $"reply echoes VCP 0x{reply[4]:X2}, expected 0x{expectedCode:X2}";
            return false;
        }

        ushort max = (ushort)((reply[6] << 8) | reply[7]);
        ushort current = (ushort)((reply[8] << 8) | reply[9]);
        value = new VcpFeatureValue(current, max);
        error = null;
        return true;
    }

    /// <summary>
    /// Decodes one capabilities-reply fragment:
    /// <c>[0x6E, len|0x80, 0xE3, offHi, offLo, data…, chk]</c>. An empty
    /// <paramref name="data"/> with success marks the end of the string.
    /// </summary>
    internal static bool TryDecodeCapabilitiesFragment(
        ReadOnlySpan<byte> reply, out ushort offset, out byte[] data, out string? error)
    {
        offset = 0;
        data = [];

        if (reply.Length < 6)
        {
            error = $"fragment too short ({reply.Length} bytes)";
            return false;
        }
        if (reply[0] != DisplayWriteAddress)
        {
            error = $"unexpected source address 0x{reply[0]:X2}";
            return false;
        }
        if ((reply[1] & 0x80) == 0)
        {
            error = $"length byte 0x{reply[1]:X2} missing protocol flag";
            return false;
        }

        int payloadLength = reply[1] & 0x7F;          // 0xE3 + offset(2) + data.
        int frameLength = 2 + payloadLength + 1;      // addr + len + payload + chk.
        if (payloadLength < 3 || reply.Length < frameLength)
        {
            error = $"declared payload {payloadLength} exceeds buffer ({reply.Length} bytes)";
            return false;
        }

        reply = reply[..frameLength];
        if (Checksum(HostReplyAddress, reply[..^1]) != reply[^1])
        {
            error = "checksum mismatch";
            return false;
        }
        if (reply[2] != 0xE3)
        {
            error = $"unexpected opcode 0x{reply[2]:X2}";
            return false;
        }

        offset = (ushort)((reply[3] << 8) | reply[4]);
        data = reply[5..^1].ToArray();
        error = null;
        return true;
    }

    /// <summary>XOR checksum seeded with the virtual address byte.</summary>
    internal static byte Checksum(byte virtualAddress, ReadOnlySpan<byte> frame)
    {
        byte chk = virtualAddress;
        foreach (byte b in frame) chk ^= b;
        return chk;
    }
}
