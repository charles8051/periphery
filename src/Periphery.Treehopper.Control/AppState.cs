// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Linq;

namespace Periphery.Treehopper.Control;

/// <summary>
/// The whole application state — the single value both front-ends render. Immutable;
/// produced only by <see cref="AppReducer.Reduce"/> folding <see cref="AppEvent"/>s.
/// </summary>
/// <param name="Boards">Known boards, in discovery order.</param>
/// <param name="SelectedBoardId">The board the UI is focused on, or null.</param>
/// <param name="FirmwareTarget">
/// The target firmware version (raw bcdDevice code) used to decide each board's
/// up-to-date / update-available status, or null when no target is known.
/// </param>
public sealed record AppState(
    ImmutableArray<BoardView> Boards,
    DeviceId? SelectedBoardId = null,
    int? FirmwareTarget = null)
{
    /// <summary>The starting state: no boards, nothing selected, no target.</summary>
    public static readonly AppState Empty = new(ImmutableArray<BoardView>.Empty);

    /// <summary>The currently selected board, or null.</summary>
    public BoardView? Selected => SelectedBoardId is { } id ? Find(id) : null;

    /// <summary>Finds a board by id, or null.</summary>
    public BoardView? Find(DeviceId id) => Boards.FirstOrDefault(b => b.Id == id);

    /// <summary>Returns a new state with the matching board transformed; no-op if absent.</summary>
    internal AppState WithBoard(DeviceId id, Func<BoardView, BoardView> transform)
    {
        int idx = IndexOf(id);
        return idx < 0 ? this : this with { Boards = Boards.SetItem(idx, transform(Boards[idx])) };
    }

    internal int IndexOf(DeviceId id)
    {
        for (int i = 0; i < Boards.Length; i++)
            if (Boards[i].Id == id) return i;
        return -1;
    }
}
