// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;

namespace Periphery.Cli.Serial.Core;

/// <summary>
/// Renders received bytes as space-separated hex, sixteen to a line. The column is
/// carried between calls because bytes arrive in arbitrary chunks.
/// </summary>
internal static class HexFormatter
{
    internal const int BytesPerLine = 16;

    internal static (int Column, string Text) Format(int column, ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            text.Append(b.ToString("X2")).Append(' ');
            if (++column == BytesPerLine)
            {
                text.Length--;
                text.Append('\n');
                column = 0;
            }
        }

        return (column, text.ToString());
    }
}
