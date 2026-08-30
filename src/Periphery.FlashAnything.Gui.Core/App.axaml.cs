// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Periphery.FlashAnything.Gui.ViewModels;

namespace Periphery.FlashAnything.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && AppHost.Service is { } service)
        {
            var vm = new MainViewModel(service);
            var window = new MainWindow { DataContext = vm };
            if (AppHost.Title is { } title) window.Title = title; // branding (DEC-006): same window, different title
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => _ = service.DisposeAsync();

            // Autonomous-run guard: an Avalonia window never exits
            // on its own, so --exit-after N closes it after N seconds — long enough to cold-start,
            // run the initial discovery snapshot, and flush the log — without a human at the window.
            if (AppHost.ExitAfterSeconds is { } seconds && seconds > 0)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) => { timer.Stop(); desktop.Shutdown(); };
                timer.Start();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
