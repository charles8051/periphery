// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Diagnostics;

/// <summary>
/// Shared <c>--verbose</c> / <c>--log-level</c> options. A command whose <c>Settings</c> derive
/// from this calls <see cref="ApplyLogging"/> early to route Periphery's internal diagnostics —
/// the <c>CM_Register_Notification</c> callbacks, device reset, and the recovery state machine —
/// to the console. Off unless requested, matching the library's no-logging default.
/// </summary>
internal abstract class DiagnosticSettings : CommandSettings
{
    [Description("Stream Periphery's internal diagnostics (CM notifications, reset, recovery) to the console at Debug.")]
    [CommandOption("-v|--verbose")]
    public bool Verbose { get; init; }

    [Description("Minimum level for --verbose: Trace, Debug, Information, Warning, Error. Implies --verbose. Default: Debug.")]
    [CommandOption("--log-level <LEVEL>")]
    public LogLevel? LogLevel { get; init; }

    /// <summary>
    /// Wire <see cref="PeripheryLoggerFactory"/> to the console when verbose/log-level was
    /// requested. Call once, before the first Periphery type is touched (the library's static
    /// loggers latch the factory on first use).
    /// </summary>
    public void ApplyLogging()
    {
        if (!Verbose && LogLevel is null) return;
        PeripheryLoggerFactory.SetLoggerFactory(
            new AnsiConsoleLoggerFactory(LogLevel ?? Microsoft.Extensions.Logging.LogLevel.Debug));
    }
}

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> that renders Periphery's logs to the Spectre console,
/// colored by level. Abstractions-only — no <c>Microsoft.Extensions.Logging.Console</c>
/// dependency — so the CLI stays lean and the output matches its house style.
/// </summary>
internal sealed class AnsiConsoleLoggerFactory(LogLevel minimum) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new AnsiConsoleLogger(categoryName, minimum);
    public void AddProvider(ILoggerProvider provider) { /* single fixed sink */ }
    public void Dispose() { }
}

internal sealed class AnsiConsoleLogger(string category, LogLevel minimum) : ILogger
{
    // Last dotted segment, e.g. "WindowsDeviceMonitorProvider" or "DeviceProxy".
    private readonly string _short =
        category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= minimum;

    public void Log<TState>(
        LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;

        var (tag, color) = level switch
        {
            LogLevel.Trace       => ("trce", "grey42"),
            LogLevel.Debug       => ("dbug", "grey"),
            LogLevel.Information => ("info", "deepskyblue1"),
            LogLevel.Warning     => ("warn", "yellow"),
            LogLevel.Error       => ("fail", "red"),
            _                    => ("crit", "red1"),
        };

        var msg = formatter(state, exception);
        AnsiConsole.MarkupLine(
            $"[grey]{DateTimeOffset.Now:HH:mm:ss.fff}[/] [{color}]{tag}[/] [dim]{Markup.Escape(_short)}[/] {Markup.Escape(msg)}");
        if (exception is not null)
            AnsiConsole.MarkupLine(
                $"      [red]{Markup.Escape(exception.GetType().Name)}: {Markup.Escape(exception.Message)}[/]");
    }
}
