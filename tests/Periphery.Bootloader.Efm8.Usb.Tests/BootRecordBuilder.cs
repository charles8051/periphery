using System;
using System.Collections.Generic;
using System.Linq;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>Hand-builds <c>$</c>-framed boot-record byte streams for tests.</summary>
internal static class BootRecordBuilder
{
    /// <summary>
    /// Builds one well-formed frame: <c>0x24</c>, length (= 1 + data.Length), command, data.
    /// </summary>
    public static byte[] Frame(byte command, params byte[] data)
    {
        if (data.Length > 254)
            throw new ArgumentException("Test frame data exceeds the one-byte length field.", nameof(data));

        var frame = new byte[3 + data.Length];
        frame[0] = Efm8Protocol.StartByte;
        frame[1] = (byte)(1 + data.Length); // command byte + data
        frame[2] = command;
        Array.Copy(data, 0, frame, 3, data.Length);
        return frame;
    }

    /// <summary>Concatenates frames into one boot-record stream.</summary>
    public static byte[] Stream(params byte[][] frames)
        => frames.SelectMany(f => f).ToArray();

    /// <summary><paramref name="count"/> deterministic data bytes.</summary>
    public static byte[] Bytes(int count)
    {
        var b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = (byte)(i * 3 + 1);
        return b;
    }
}
