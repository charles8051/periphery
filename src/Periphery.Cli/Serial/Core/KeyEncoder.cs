// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;

namespace Periphery.Cli.Serial.Core;

/// <summary>What Enter sends.</summary>
internal enum LineEnding
{
    Cr,
    Lf,
    CrLf,
}

/// <summary>The outcome of one keystroke.</summary>
internal abstract record KeyAction
{
    /// <summary>Write these bytes to the port.</summary>
    internal sealed record Send(byte[] Bytes) : KeyAction;

    /// <summary>The quit key. Nothing is sent.</summary>
    internal sealed record Quit : KeyAction;

    /// <summary>Nothing to send, e.g. a bare modifier or the first half of a surrogate pair.</summary>
    internal sealed record Ignore : KeyAction;
}

/// <summary>
/// Turns console keystrokes into the bytes a serial terminal sends. Enter becomes
/// the configured line ending, navigation keys become VT100 sequences, Ctrl+] quits,
/// and every other key is sent as the UTF-8 of its character, control characters
/// (Ctrl+C as 0x03) included.
/// </summary>
/// <remarks>
/// <see cref="Console.ReadKey(bool)"/> delivers a character outside the BMP as two
/// keystrokes, one per UTF-16 surrogate. The high half is carried in
/// <see cref="PendingHighSurrogate"/> until its partner arrives.
/// </remarks>
internal sealed record KeyEncoder(LineEnding LineEnding, char? PendingHighSurrogate = null)
{
    /// <summary>Ctrl+], the telnet and miniterm escape.</summary>
    internal const char QuitChar = '\x1D';

    internal (KeyEncoder Next, KeyAction Action) Encode(ConsoleKeyInfo key)
    {
        char c = key.KeyChar;

        if (c == QuitChar)
            return (this with { PendingHighSurrogate = null }, new KeyAction.Quit());

        if (key.Key == ConsoleKey.Enter)
            return (this with { PendingHighSurrogate = null }, new KeyAction.Send(LineEndingBytes(LineEnding)));

        if (c == '\0')
        {
            byte[]? sequence = NavigationSequence(key.Key);
            return sequence is null
                ? (this, new KeyAction.Ignore())
                : (this with { PendingHighSurrogate = null }, new KeyAction.Send(sequence));
        }

        if (char.IsHighSurrogate(c))
            return (this with { PendingHighSurrogate = c }, new KeyAction.Ignore());

        if (char.IsLowSurrogate(c))
        {
            if (PendingHighSurrogate is not { } high)
                return (this, new KeyAction.Ignore());
            byte[] pair = Encoding.UTF8.GetBytes(new[] { high, c });
            return (this with { PendingHighSurrogate = null }, new KeyAction.Send(pair));
        }

        return (this with { PendingHighSurrogate = null }, new KeyAction.Send(Encoding.UTF8.GetBytes(new[] { c })));
    }

    internal static byte[] LineEndingBytes(LineEnding ending) => ending switch
    {
        LineEnding.Lf => "\n"u8.ToArray(),
        LineEnding.CrLf => "\r\n"u8.ToArray(),
        _ => "\r"u8.ToArray(),
    };

    private static byte[]? NavigationSequence(ConsoleKey key) => key switch
    {
        ConsoleKey.UpArrow => "\e[A"u8.ToArray(),
        ConsoleKey.DownArrow => "\e[B"u8.ToArray(),
        ConsoleKey.RightArrow => "\e[C"u8.ToArray(),
        ConsoleKey.LeftArrow => "\e[D"u8.ToArray(),
        ConsoleKey.Home => "\e[H"u8.ToArray(),
        ConsoleKey.End => "\e[F"u8.ToArray(),
        ConsoleKey.Insert => "\e[2~"u8.ToArray(),
        ConsoleKey.Delete => "\e[3~"u8.ToArray(),
        ConsoleKey.PageUp => "\e[5~"u8.ToArray(),
        ConsoleKey.PageDown => "\e[6~"u8.ToArray(),
        _ => null,
    };
}
