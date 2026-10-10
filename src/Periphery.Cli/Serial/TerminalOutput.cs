// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;
using Periphery.Cli.Serial.Core;
using Spectre.Console;

namespace Periphery.Cli.Serial;

/// <summary>
/// Everything the terminal prints. Port data goes to stdout: decoded as UTF-8 on a
/// console, unchanged when stdout is redirected, or rendered as hex. Status lines go to stderr. When both share a screen, a status
/// line that would land mid-line first ends the data line; when stdout is redirected
/// the data is left untouched. One lock serialises the read pump, the keyboard echo,
/// and status changes raised on the session host's threads.
/// </summary>
internal sealed class TerminalOutput
{
    private readonly object _lock = new();
    private readonly IAnsiConsole _status;
    private readonly TextWriter _data;
    private readonly Stream? _raw;
    private readonly bool _hex;
    private readonly bool _sharedScreen;

    // Stateful because a multi-byte character can straddle two reads.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    private StatusNarrator _narrator;
    private int _hexColumn;
    private bool _atLineStart = true;

    /// <param name="raw">
    /// Where text-mode data is copied byte for byte, or <see langword="null"/> to decode it
    /// onto <paramref name="data"/>. A redirected stdout wants the port's bytes; a console
    /// wants characters, because <see cref="Console.Out"/> re-encodes to the console code page.
    /// </param>
    internal TerminalOutput(
        IAnsiConsole status, TextWriter data, Stream? raw, bool hex, bool sharedScreen, StatusNarrator narrator)
    {
        _sharedScreen = sharedScreen;
        _status = status;
        _data = data;
        _raw = raw;
        _hex = hex;
        _narrator = narrator;
    }

    internal void WriteData(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;

        lock (_lock)
        {
            if (_hex)
            {
                (_hexColumn, string text) = HexFormatter.Format(_hexColumn, bytes);
                _data.Write(text);
                _atLineStart = _hexColumn == 0;
            }
            else if (_raw is not null)
            {
                _raw.Write(bytes);
                _raw.Flush();
                _atLineStart = bytes[^1] == (byte)'\n';
                return;
            }
            else
            {
                var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
                int count = _decoder.GetChars(bytes, chars, flush: false);
                if (count == 0) return;
                _data.Write(chars, 0, count);
                _atLineStart = chars[count - 1] == '\n';
            }

            _data.Flush();
        }
    }

    internal void Narrate<TSession>(HostStatus<TSession> status) where TSession : class
    {
        lock (_lock)
        {
            (_narrator, string? line) = _narrator.Next(status);
            if (line is not null) WriteStatusLocked(line);
        }
    }

    /// <summary>Ends a partial data line on a shared screen, so the shell prompt starts clean.</summary>
    internal void Finish()
    {
        lock (_lock)
        {
            if (!_sharedScreen || _atLineStart) return;
            _data.WriteLine();
            _data.Flush();
            _atLineStart = true;
        }
    }

    internal void Status(string line)
    {
        lock (_lock) WriteStatusLocked(line);
    }

    private void WriteStatusLocked(string line)
    {
        if (_sharedScreen && !_atLineStart)
        {
            _data.WriteLine();
            _data.Flush();
            _atLineStart = true;
            _hexColumn = 0;
        }

        _status.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
    }
}
