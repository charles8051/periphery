// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Periphery;
using Periphery.Bootloader;
using Periphery.Diagnostics;
using Periphery.FlashAnything;

namespace Periphery.FlashAnything.Gui;

/// <summary>
/// The reusable FlashAnything GUI host: builds the service from an injected composition, applies
/// branding, and launches the shared Avalonia app. The generic <c>FlashAnything</c> GUI and a branded
/// device-specific flasher GUI (ADR-0063 DEC-006) are both thin <c>[STAThread] Main</c>s over
/// <see cref="Run"/> — same window, view-model, and discovery; only the registry + title differ.
/// </summary>
public static class GuiHost
{
    /// <summary>
    /// Runs the GUI over the composition built by <paramref name="serviceFactory"/>, titled
    /// <paramref name="title"/>. Call from a <c>[STAThread] Main</c>.
    /// </summary>
    public static int Run(
        Func<ILogger?, BootloaderEntryOptions?, FlashAnythingService> serviceFactory, string title, string[] args)
    {
        var opts = LaunchOptions.Parse(args);

        // Wire Periphery's logging FIRST. Its watcher/providers capture a *static* logger at
        // type-init, so the factory must be set before the service ctor touches any device type
        // (Devices.Watch()), or those statics latch onto the NullLogger and the discovery trace is
        // lost. --log-file mirrors the full DEBUG trace for the autonomous debug loop.
        SinkLoggerFactory? loggerFactory = null;
        ILogger? logger = null;
        if (opts.LogFile is { } logFile)
        {
            loggerFactory = new SinkLoggerFactory(new FileLogSink(logFile, title), LogLevel.Debug);
            PeripheryLoggerFactory.SetLoggerFactory(loggerFactory);
            logger = loggerFactory.CreateLogger(title);
        }

        // The GUI exposes no bootloader-entry tunables; the composition's defaults apply.
        AppHost.Service = serviceFactory(logger, null);
        AppHost.Title = title;
        AppHost.ExitAfterSeconds = opts.ExitAfterSeconds;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnLastWindowClose);
        }
        finally
        {
            loggerFactory?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>Hands the app service, branding, and autonomous-run options to <see cref="App"/> at framework-init time.</summary>
public static class AppHost
{
    public static FlashAnythingService? Service;

    /// <summary>The window title / product branding (e.g. "FlashAnything", "Treehopper Flasher").</summary>
    public static string? Title;

    /// <summary>When set, the window auto-closes after this many seconds (for the autonomous debug loop).</summary>
    public static int? ExitAfterSeconds;
}

/// <summary>Parsed launch flags: an opt-in file log and an auto-exit timer for unattended runs.</summary>
internal readonly record struct LaunchOptions(string? LogFile, int? ExitAfterSeconds)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? logFile = null;
        int? exitAfter = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log-file" when i + 1 < args.Length:
                    logFile = args[++i];
                    break;
                case "--exit-after" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s):
                    exitAfter = s;
                    i++;
                    break;
            }
        }
        return new LaunchOptions(logFile, exitAfter);
    }
}
