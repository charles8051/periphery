// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Periphery;

/// <summary>
/// Global logging configuration for Periphery.
/// Consumers can optionally inject an <see cref="ILoggerFactory"/> to enable diagnostic logging.
/// </summary>
/// <remarks>
/// <para>
/// By default, Periphery performs no logging. To enable logging, call
/// <see cref="SetLoggerFactory"/> with your application's logger factory:
/// </para>
/// <code>
/// var loggerFactory = LoggerFactory.Create(builder =>
/// {
///     builder.AddConsole();
///     builder.SetMinimumLevel(LogLevel.Information);
/// });
/// PeripheryLoggerFactory.SetLoggerFactory(loggerFactory);
/// </code>
/// <para>
/// Logging is conservative and high-level:
/// </para>
/// <list type="bullet">
/// <item><b>Information:</b> Device enumeration start/completion with counts</item>
/// <item><b>Warning:</b> Individual device parsing failures (enumeration continues)</item>
/// <item><b>Error:</b> Provider initialization failures, Win32/SetupAPI errors</item>
/// <item><b>Debug:</b> Detailed query execution, filter application</item>
/// </list>
/// </remarks>
public static class PeripheryLoggerFactory
{
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// Configure the logger factory for all Periphery operations.
    /// Pass <c>null</c> to disable logging.
    /// </summary>
    /// <param name="loggerFactory">The logger factory to use, or null to disable logging.</param>
    public static void SetLoggerFactory(ILoggerFactory? loggerFactory)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Create a logger for the specified category.
    /// </summary>
    internal static ILogger<T> CreateLogger<T>() => _loggerFactory.CreateLogger<T>();

    /// <summary>
    /// Create a logger for the specified category name.
    /// </summary>
    internal static ILogger CreateLogger(string categoryName) => _loggerFactory.CreateLogger(categoryName);
}
