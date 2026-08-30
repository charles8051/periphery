// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Periphery.FlashAnything.Gui.ViewModels;

namespace Periphery.FlashAnything.Gui;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // The file dialog is a view concern (it needs the window's StorageProvider); the path
    // it yields is handed to the view-model, which owns the load/flash logic.
    private async void OnPickFirmware(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select firmware image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Firmware (.bin, .hex, .elf)")
                    { Patterns = ["*.bin", "*.hex", "*.elf", "*.axf", "*.out"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await vm.SetFirmwareAsync(path);
    }
}
