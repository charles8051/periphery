// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Periphery.Bootloader;
using Periphery.FlashAnything;

namespace Periphery.FlashAnything.Gui.ViewModels;

/// <summary>
/// Binds the shared <see cref="FlashAnythingService"/> to the window: projects
/// <c>AppState</c> to bindable rows, picks/loads firmware, and arms/disarms autoflash.
/// The service owns the state; this VM only renders it and forwards intents.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly FlashAnythingService _service;

    [ObservableProperty]
    private string _statusLine = "Discovering...";

    [ObservableProperty]
    private string _firmwareSummary = "No firmware loaded.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ArmCommand))]
    private string? _selectedFamily;

    [ObservableProperty]
    private string _autoflashStatus = "Disarmed.";

    [ObservableProperty]
    private bool _isArmed;

    /// <summary>The provider/family names the operator can arm autoflash for.</summary>
    public IReadOnlyList<string> Families { get; }

    public ObservableCollection<TargetRow> Targets { get; } = new();

    public MainViewModel(FlashAnythingService service)
    {
        _service = service;
        Families = service.KnownFamilies;
        _selectedFamily = Families.Count > 0 ? Families[0] : null;
        _service.StateChanged += OnStateChanged;
        OnStateChanged(_service.State);
        _ = RunInitialDiscoveryAsync(); // initial discovery
    }

    // Fire-and-forget discovery, but observe the task: RefreshAsync logs-then-rethrows a failed
    // initial snapshot, so an unobserved faulted task would otherwise surface on the finalizer.
    private async Task RunInitialDiscoveryAsync()
    {
        try { await _service.RefreshAsync(); }
        catch { /* the service already logged the failure; surfacing it into AppState is a follow-up */ }
    }

    private void OnStateChanged(AppState state)
    {
        // Service events arrive on thread-pool threads; marshal to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            // Reconcile in place (match by id) so the ListBox scroll position survives the
            // frequent progress updates during a flash.
            var existing = Targets.ToDictionary(t => t.Id);
            foreach (var t in state.Targets)
            {
                string mode = t.RebootsToFlash ? " (application)" : "";
                string summary = $"{t.DisplayName} [{t.ProviderName}]{mode} - {t.Stage} {t.Percent}%"
                    + (t.LastError is { } error ? $" - {error}" : "");
                if (existing.Remove(t.Id, out var row))
                    row.Summary = summary;
                else
                    Targets.Add(new TargetRow(t.Id) { Summary = summary });
            }
            foreach (var gone in existing.Values)
                Targets.Remove(gone);

            StatusLine = state.Targets.Length == 0
                ? "No flashable targets - connect a device in bootloader mode (e.g. STM32 DFU 0483:DF11)."
                : $"{state.Targets.Length} flashable target(s).";
            FirmwareSummary = state.Firmware is { } fw
                ? $"Firmware: {fw.DisplayName} ({fw.Size} bytes)"
                : state.FirmwareError is { } err ? err : "No firmware loaded.";

            // Only targets with a port can be bound as a fixture.
            var ports = state.Targets
                .Where(t => t.PortName is not null)
                .Select(t => t.PortName!.Value.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var gone in Ports.Where(x => !ports.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList())
                Ports.Remove(gone);
            foreach (var added in ports.Where(x => !Ports.Contains(x, StringComparer.OrdinalIgnoreCase)))
                Ports.Add(added);
            if (SelectedPort is { } chosen && !Ports.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                SelectedPort = null;

            IsArmed = state.Autoflash is not null;
            AutoflashStatus = state.Autoflash is { } cfg
                ? $"ARMED [{cfg.Family}]{(cfg.Bridges.IsEmpty ? "" : $" on {SelectedPort}")}  " +
                  $"{(state.AutoflashTally.CountsDistinctBoards ? "flashed" : "flashes")} {state.AutoflashTally.Flashed}" +
                  $" / failed {state.AutoflashTally.Failed} / skipped {state.AutoflashTally.Skipped}"
                : "Disarmed.";
            ArmCommand.NotifyCanExecuteChanged();
            DisarmCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>Loads a firmware image chosen by the view's file dialog (a raw .bin at the STM32 base; .hex and .elf carry their own addresses).</summary>
    public Task SetFirmwareAsync(string path) => _service.DispatchAsync(new AppIntent.LoadFirmware(path, 0x08000000));

    /// <summary>
    /// The serial fixtures on offer to bind, for a probe-identified family. Populated from the
    /// detected targets that have a port.
    /// </summary>
    public ObservableCollection<string> Ports { get; } = new();

    /// <summary>
    /// The fixture the operator picked. A probe family cannot be armed without one: its bridge's
    /// VID/PID names the bridge, never the part behind it, so autoflash has no way to know which
    /// fixture was meant (adr.md Decision 8).
    /// </summary>
    [ObservableProperty] private string? _selectedPort;

    /// <summary>
    /// Whether a bound fixture may flash a succession of boards. Off by default: departure is
    /// inferred from silence, which cannot tell a board that left from one that reset in place.
    /// </summary>
    [ObservableProperty] private bool _repeat;

    /// <summary>True when the chosen family needs a fixture bound before it can be armed.</summary>
    public bool NeedsPort => SelectedFamily is { } family && _service.FamilyNeedsPort(family);

    private bool CanArm() => _service.State.Firmware is not null
        && !string.IsNullOrEmpty(SelectedFamily) && _service.State.Autoflash is null
        && (!NeedsPort || !string.IsNullOrEmpty(SelectedPort));

    [RelayCommand(CanExecute = nameof(CanArm))]
    private Task Arm() => _service.DispatchAsync(new AppIntent.ArmAutoflash(
        SelectedFamily!,
        FlashOptions.Default,
        SelectedPort is { } port ? [new SerialPortName(port)] : ImmutableArray<SerialPortName>.Empty,
        Repeat ? RepeatMode.Silence : RepeatMode.None));

    private bool CanDisarm() => _service.State.Autoflash is not null;

    [RelayCommand(CanExecute = nameof(CanDisarm))]
    private Task Disarm() => _service.DispatchAsync(new AppIntent.DisarmAutoflash());
}

/// <summary>A bindable row for one flashable target; <see cref="Summary"/> updates live during flashing.</summary>
public partial class TargetRow : ObservableObject
{
    public TargetRow(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty]
    private string _summary = "";
}
