// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Periphery.Treehopper.Control;
using Periphery.Treehopper.Control.Gui.ViewModels;

namespace Periphery.Treehopper.Control.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var service = new TreehopperControlService();
            var vm = new MainViewModel(service);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => _ = vm.DisposeAsync();
            _ = vm.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
