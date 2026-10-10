// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Cli.Serial.Core;

/// <summary>
/// Makes device-reported text safe to print on a terminal. A device's name and maker come
/// from its descriptors, and an ESC or other control character there would reach the
/// terminal as a cursor move, a title change or an OSC sequence. Markup escaping does not
/// remove them.
/// </summary>
internal static class TerminalText
{
    /// <summary>Each control character (C0, DEL, C1) replaced with U+FFFD.</summary>
    internal static string Printable(string text)
    {
        if (!text.Any(char.IsControl)) return text;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? '�' : source[i];
        });
    }
}
