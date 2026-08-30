using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// A minimal <see cref="ILogger"/> that records every entry's level and fully-rendered message, so a
/// test can assert on the diagnostic lines the uploader emits (e.g. that a non-ack warning carries the
/// full reply report). Renders through the supplied formatter, exactly as a real sink would.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    public readonly record struct Entry(LogLevel Level, string Message);

    public List<Entry> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new Entry(logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
