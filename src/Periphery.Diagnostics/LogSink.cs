// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Diagnostics;

/// <summary>
/// Destination for already-formatted log entries. An implementation owns the underlying IO
/// resource (a file handle, the console stream) and serializes writes so concurrent loggers
/// don't interleave. Abstractions-only, so the whole unit stays AOT-clean for the self-contained
/// GUI publish.
/// </summary>
/// <remarks>
/// This is the single variation point of <see cref="SinkLoggerProvider"/>: the provider owns the
/// line format (timestamp, level, category, message, exception); the sink owns where the bytes
/// go. A future tee sink (file <em>and</em> console) would slot in here without touching the
/// provider.
/// </remarks>
public interface ILogSink : IDisposable
{
    /// <summary>Append one formatted entry (which may span several lines). Must be thread-safe.</summary>
    void Write(string entry);
}

/// <summary>
/// A file-backed <see cref="ILogSink"/>: creates (truncating) the file and flushes after every
/// write so the log survives a crash mid-run. Pair with a <c>--log-file &lt;path&gt;</c> flag — this
/// is what mirrors Periphery's full DEBUG trace for an "observable example" loop.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    /// <summary>
    /// Opens (and truncates) <paramref name="path"/>, creating its directory if needed. When
    /// <paramref name="logName"/> is given, writes a <c># {logName} log — opened {timestamp}</c>
    /// banner as the first line.
    /// </summary>
    public FileLogSink(string path, string? logName = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        if (logName is not null)
            _writer.WriteLine($"# {logName} log — opened {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
    }

    public void Write(string entry)
    {
        lock (_gate)
            _writer.WriteLine(entry);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}

/// <summary>
/// A console-backed <see cref="ILogSink"/> that writes to <c>stderr</c>, keeping a command's real
/// output on <c>stdout</c> clean and pipeable. Used by the CLI's <c>--verbose</c> diagnostics.
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly object _gate = new();

    public void Write(string entry)
    {
        lock (_gate)
            Console.Error.WriteLine(entry);
    }

    public void Dispose() { }
}
