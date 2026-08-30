// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Periphery.Treehopper.Control;

namespace Periphery.Treehopper.Control.Gui.ViewModels;

/// <summary>One board. Created once per discovered board; pins and fields updated in place.</summary>
public partial class BoardViewModel : ObservableObject
{
    public DeviceId Id { get; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _versionText = "v?";
    [ObservableProperty] private string _firmwareText = "";
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private string? _i2cText;
    [ObservableProperty] private string? _lastError;

    /// <summary>The 20 pins, created once; never rebuilt (so the UI doesn't churn on reports).</summary>
    public ObservableCollection<PinViewModel> Pins { get; } = new();

    public BoardViewModel(BoardView b, Func<AppIntent, Task> dispatch)
    {
        Id = b.Id;
        foreach (var p in b.Pins)
            Pins.Add(new PinViewModel(b.Id, p.Number, dispatch));
        Update(b);
    }

    public void Update(BoardView b)
    {
        Label = b.Label;
        VersionText = b.Version is int v ? FirmwareVersion.Describe(v) : "v?";
        FirmwareText = StatusText(b.Firmware);
        ConnectionText = b.Connection.ToString();
        LastError = b.LastError;
        I2cText = b.I2cResponders is { } r
            ? "I2C: " + (r.Length == 0 ? "(none)" : string.Join(" ", r.Select(x => $"0x{x:X2}")))
            : null;

        int n = Math.Min(Pins.Count, b.Pins.Length);
        for (int i = 0; i < n; i++)
            Pins[i].Update(b.Pins[i]);
    }

    private static string StatusText(FirmwareView f) => f.Status switch
    {
        FirmwareStatus.Updating => $"updating {f.Percent}%",
        FirmwareStatus.Failed => $"FAILED: {f.Message}",
        FirmwareStatus.UpdateAvailable => "update available",
        FirmwareStatus.UpToDate => "up to date",
        FirmwareStatus.Updated => "updated",
        _ => "version unknown",
    };
}
