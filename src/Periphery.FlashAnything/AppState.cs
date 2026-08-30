// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Linq;

namespace Periphery.FlashAnything;

/// <summary>
/// The whole application state — the single value both front-ends render. Immutable;
/// produced only by <see cref="AppReducer.Reduce"/> folding <see cref="AppEvent"/>s.
/// </summary>
public sealed record AppState(
    ImmutableArray<FlashTargetView> Targets,
    DeviceId? SelectedTargetId = null,
    FirmwareSelection? Firmware = null,
    string? FirmwareError = null,
    AutoflashConfig? Autoflash = null)
{
    /// <summary>The starting state: no targets, nothing selected, no firmware loaded.</summary>
    public static readonly AppState Empty = new(ImmutableArray<FlashTargetView>.Empty);

    /// <summary>Running autoflash session tally (flashed/failed/skipped + audit); resets on arm.</summary>
    public AutoflashTally AutoflashTally { get; init; } = AutoflashTally.Empty;

    /// <summary>The currently selected target, or null.</summary>
    public FlashTargetView? Selected => SelectedTargetId is { } id ? Find(id) : null;

    // Device ids compare case-insensitively: Windows re-enumerates the same USB device with different
    // casing across a reset, so a returned board must resolve to its existing row, not a new one.
    // That invariant now lives in the DeviceId type rather than in a per-call-site comparer.
    /// <summary>Finds a target by id, or null.</summary>
    public FlashTargetView? Find(DeviceId id) => Targets.FirstOrDefault(t => t.Id == id);

    /// <summary>Returns a new state with the matching target transformed; no-op if absent.</summary>
    internal AppState WithTarget(DeviceId id, Func<FlashTargetView, FlashTargetView> transform)
    {
        int idx = IndexOf(id);
        return idx < 0 ? this : this with { Targets = Targets.SetItem(idx, transform(Targets[idx])) };
    }

    internal int IndexOf(DeviceId id)
    {
        for (int i = 0; i < Targets.Length; i++)
            if (Targets[i].Id == id) return i;
        return -1;
    }
}

/// <summary>The firmware image the user has chosen to flash.</summary>
public sealed record FirmwareSelection(string Path, string DisplayName, long Size);

/// <summary>A per-device autoflash outcome kind, folded into the session tally.</summary>
public enum AutoflashOutcomeKind { Flashed, Failed, Skipped }

/// <summary>Running autoflash session tally: counts + a human-readable audit list (newest last).</summary>
public sealed record AutoflashTally(int Flashed, int Failed, int Skipped, ImmutableArray<string> Audit)
{
    /// <summary>The empty tally (the start of an armed session).</summary>
    public static readonly AutoflashTally Empty = new(0, 0, 0, ImmutableArray<string>.Empty);

    /// <summary>Total devices acted on (flashed + failed + skipped).</summary>
    public int Total => Flashed + Failed + Skipped;

    /// <summary>Fold one per-device outcome into the tally.</summary>
    public AutoflashTally With(AutoflashOutcomeKind kind, DeviceId id, string? detail)
    {
        string suffix = detail is null ? "" : $": {detail}";
        return kind switch
        {
            AutoflashOutcomeKind.Flashed => this with { Flashed = Flashed + 1, Audit = Audit.Add($"flashed {id}") },
            AutoflashOutcomeKind.Failed  => this with { Failed = Failed + 1, Audit = Audit.Add($"failed {id}{suffix}") },
            _                            => this with { Skipped = Skipped + 1, Audit = Audit.Add($"skipped {id}{suffix}") },
        };
    }
}
