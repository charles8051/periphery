// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Periphery.Treehopper.Control;

namespace Periphery.Treehopper.Control.Gui.ViewModels;

/// <summary>
/// The one view-model both halves of the window bind to. Subscribes to the service's
/// <see cref="TreehopperControlService.StateChanged"/> and reconciles each immutable
/// <see cref="AppState"/> into the bindable VM tree in place (match-by-key, update fields),
/// so frequent report updates never churn the UI. User gestures dispatch <see cref="AppIntent"/>s.
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TreehopperControlService _service;
    private bool _suppressSelectionDispatch;

    public ObservableCollection<BoardViewModel> Boards { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanI2cCommand))]
    private BoardViewModel? _selectedBoard;

    [ObservableProperty] private string _status = "Starting…";

    public MainViewModel(TreehopperControlService service)
    {
        _service = service;
        _service.StateChanged += OnStateChanged;
    }

    public async Task StartAsync()
    {
        try { await _service.StartAsync(); }
        catch (Exception ex) { Status = $"Start failed: {ex.Message}"; return; }
        Apply(_service.State);
    }

    private void OnStateChanged(object? sender, AppState state)
    {
        if (Dispatcher.UIThread.CheckAccess()) Apply(state);
        else Dispatcher.UIThread.Post(() => Apply(state));
    }

    private void Apply(AppState state)
    {
        // Everything here mirrors the service's state, so nothing in it may dispatch back —
        // including the selection change the list control raises when the SELECTED board is the
        // one removed below. The reducer already decides what happens to the selection in that
        // case (fall back to the first remaining board, else nothing), and a SelectBoard(null)
        // from here would clobber that choice.
        _suppressSelectionDispatch = true;
        try
        {
            // Remove boards that are gone.
            for (int i = Boards.Count - 1; i >= 0; i--)
                if (state.Find(Boards[i].Id) is null)
                    Boards.RemoveAt(i);

            // Add new / update existing, in place.
            foreach (var b in state.Boards)
            {
                var existing = Boards.FirstOrDefault(x => x.Id == b.Id);
                if (existing is null) Boards.Add(new BoardViewModel(b, i => _service.DispatchAsync(i)));
                else existing.Update(b);
            }

            SelectedBoard = state.SelectedBoardId is { } id ? Boards.FirstOrDefault(x => x.Id == id) : null;
        }
        finally
        {
            _suppressSelectionDispatch = false;
        }

        Status = Boards.Count switch
        {
            0 => "No boards connected",
            1 => "1 board",
            var n => $"{n} boards",
        };
    }

    partial void OnSelectedBoardChanged(BoardViewModel? value)
    {
        if (_suppressSelectionDispatch) return;
        _ = SelectAsync(value?.Id);
    }

    // DeviceId?, not string?: AppIntent.SelectBoard takes DeviceId? and a null string would be
    // converted through DeviceId's implicit operator, which throws on null. That is not
    // hypothetical — removing the selected board below deselects it, and the resulting
    // SelectBoard(null) faulted into the discarded task, so the deselect never landed and live
    // streaming stayed on for a board that was gone.
    private async Task SelectAsync(DeviceId? id)
    {
        await _service.DispatchAsync(new AppIntent.SelectBoard(id));
        // Selecting a board opens its session and streams live pin state.
        await _service.SetLiveStreamingAsync(id is not null);
    }

    [RelayCommand]
    private Task Refresh() => _service.DispatchAsync(new AppIntent.RefreshBoards());

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ScanI2c() =>
        SelectedBoard is { } b ? _service.DispatchAsync(new AppIntent.ScanI2c(b.Id)) : Task.CompletedTask;

    /// <summary>
    /// Flashes the selected board with a file-picked firmware image. Invoked from the
    /// view's file picker. <paramref name="fileName"/> drives format inference
    /// (.hex Intel HEX vs .tfi/.efm8 boot records) and content verification in the service.
    /// </summary>
    public Task FlashSelectedAsync(byte[] firmware, string fileName) =>
        SelectedBoard is { } b ? _service.FlashAsync(b.Id, firmware, fileName) : Task.CompletedTask;

    private bool HasSelection => SelectedBoard is not null;

    public async ValueTask DisposeAsync()
    {
        _service.StateChanged -= OnStateChanged;
        await _service.DisposeAsync();
    }
}
