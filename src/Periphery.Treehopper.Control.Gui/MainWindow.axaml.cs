// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Periphery.Treehopper.Control.Gui.ViewModels;

namespace Periphery.Treehopper.Control.Gui;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    // Firmware flashing needs a file picker (TopLevel.StorageProvider), so it lives in
    // code-behind; picking the file is the deliberate confirmation, and the service still
    // requires the explicit Efm8FlashConfirmation internally.
    private async void OnFlashClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedBoard is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Treehopper firmware (.hex / .tfi / .efm8)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Treehopper firmware") { Patterns = new[] { "*.hex", "*.tfi", "*.efm8" } },
            },
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        byte[] image;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            image = ms.ToArray();
        }
        catch (Exception)
        {
            return; // unreadable file; the service would surface nothing to flash
        }

        // The service infers the format from the file name and verifies it against the
        // content (a mismatched/wrong file is refused there and surfaced as a failure).
        await vm.FlashSelectedAsync(image, file.Name);
    }
}
