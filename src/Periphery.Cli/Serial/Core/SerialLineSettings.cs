// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO.Ports;

namespace Periphery.Cli.Serial.Core;

/// <summary>
/// The line settings a terminal opens a port with, and their parsing from the
/// command-line spellings.
/// </summary>
internal sealed record SerialLineSettings(
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    Handshake Handshake)
{
    internal static readonly SerialLineSettings Default =
        new(115200, 8, Parity.None, StopBits.One, Handshake.None);

    /// <summary>The conventional short form, e.g. <c>115200 8N1</c>.</summary>
    internal string Describe()
    {
        string text = $"{BaudRate} {DataBits}{ParityLetter(Parity)}{StopBitsText(StopBits)}";
        return Handshake == Handshake.None ? text : $"{text} {HandshakeName(Handshake)}";
    }

    internal static bool TryParseParity(string text, out Parity parity)
    {
        parity = text.ToLowerInvariant() switch
        {
            "n" or "none" => Parity.None,
            "e" or "even" => Parity.Even,
            "o" or "odd" => Parity.Odd,
            "m" or "mark" => Parity.Mark,
            "s" or "space" => Parity.Space,
            _ => (Parity)(-1),
        };
        return parity != (Parity)(-1);
    }

    internal static bool TryParseStopBits(string text, out StopBits stopBits)
    {
        stopBits = text switch
        {
            "1" => StopBits.One,
            "1.5" => StopBits.OnePointFive,
            "2" => StopBits.Two,
            _ => StopBits.None,
        };
        return stopBits != StopBits.None;
    }

    internal static bool TryParseHandshake(string text, out Handshake handshake)
    {
        handshake = text.ToLowerInvariant() switch
        {
            "none" => Handshake.None,
            "rtscts" => Handshake.RequestToSend,
            "xonxoff" => Handshake.XOnXOff,
            "both" => Handshake.RequestToSendXOnXOff,
            _ => (Handshake)(-1),
        };
        return handshake != (Handshake)(-1);
    }

    private static char ParityLetter(Parity parity) => parity switch
    {
        Parity.Even => 'E',
        Parity.Odd => 'O',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => 'N',
    };

    private static string StopBitsText(StopBits stopBits) => stopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1",
    };

    private static string HandshakeName(Handshake handshake) => handshake switch
    {
        Handshake.RequestToSend => "rtscts",
        Handshake.XOnXOff => "xonxoff",
        _ => "rtscts+xonxoff",
    };
}
