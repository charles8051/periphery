// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace Periphery.Diagnostics;

/// <summary>
/// A minimal <see cref="ILoggerProvider"/> that formats each entry as a single line —
/// <c>[HH:mm:ss.fff] LVL [Category] message</c>, with any exception's type, message, and stack
/// trace on continuation lines — and writes it to an <see cref="ILogSink"/>. The sink (file or
/// console) is the only variation point; the format is shared. Abstractions-only, so it stays
/// AOT-clean. Replaces the three near-identical hand-rolled providers flagged in
/// docs/explorations/architecture-deepening-review-2026-06 (§6.4).
/// </summary>
public sealed class SinkLoggerProvider : ILoggerProvider
{
    private readonly ILogSink _sink;
    private readonly LogLevel _minLevel;

    /// <param name="sink">Where formatted entries go. The provider takes ownership and disposes it.</param>
    /// <param name="minLevel">Entries below this level are dropped.</param>
    public SinkLoggerProvider(ILogSink sink, LogLevel minLevel = LogLevel.Debug)
    {
        _sink = sink;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new SinkLogger(_sink, _minLevel, categoryName);

    public void Dispose() => _sink.Dispose();

    private sealed class SinkLogger(ILogSink sink, LogLevel minLevel, string category) : ILogger
    {
        private readonly string _category = GetShortCategory(category);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???",
            };

            var entry = $"[{timestamp}] {level} [{_category}] {formatter(state, exception)}";
            if (exception is not null)
                entry += $"\n  {exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";

            sink.Write(entry);
        }

        private static string GetShortCategory(string category)
        {
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 ? category[(lastDot + 1)..] : category;
        }
    }
}

/// <summary>
/// A one-sink <see cref="ILoggerFactory"/> over <see cref="SinkLoggerProvider"/> — enough to feed
/// Periphery's <c>PeripheryLoggerFactory</c> (and an app's own logger) a single fixed mirror without
/// pulling in the full Microsoft.Extensions.Logging builder. Abstractions-only, so it stays
/// AOT-clean. A host that already builds a Microsoft.Extensions.Logging pipeline (e.g. an example
/// using <c>LoggerFactory.Create</c>) should add a <see cref="SinkLoggerProvider"/> as a provider
/// instead.
/// </summary>
public sealed class SinkLoggerFactory : ILoggerFactory
{
    private readonly SinkLoggerProvider _provider;

    public SinkLoggerFactory(ILogSink sink, LogLevel minLevel = LogLevel.Debug)
        => _provider = new SinkLoggerProvider(sink, minLevel);

    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    public void AddProvider(ILoggerProvider provider) { /* single fixed sink */ }

    public void Dispose() => _provider.Dispose();
}
