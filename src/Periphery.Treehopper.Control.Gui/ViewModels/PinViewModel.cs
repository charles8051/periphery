// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Periphery.Treehopper;
using Periphery.Treehopper.Control;

namespace Periphery.Treehopper.Control.Gui.ViewModels;

/// <summary>One pin row. Updated in place from <see cref="PinView"/>; its commands dispatch intents.</summary>
public partial class PinViewModel : ObservableObject
{
    private readonly DeviceId _boardId;
    private readonly Func<AppIntent, Task> _dispatch;

    public int Number { get; }

    [ObservableProperty] private PinMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    private bool _high;

    [ObservableProperty] private int _adc;

    public PinViewModel(DeviceId boardId, int number, Func<AppIntent, Task> dispatch)
    {
        _boardId = boardId;
        Number = number;
        _dispatch = dispatch;
    }

    public string LevelText => High ? "HIGH" : "low";

    public void Update(PinView p)
    {
        Mode = p.Mode;
        High = p.High;
        Adc = p.Adc;
    }

    [RelayCommand]
    private Task Toggle() => _dispatch(new AppIntent.ToggleOutput(_boardId, Number));

    [RelayCommand]
    private Task MakeInput() => _dispatch(new AppIntent.SetPinMode(_boardId, Number, PinMode.DigitalInput));
}
