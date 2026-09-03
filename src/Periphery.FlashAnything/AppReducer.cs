// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// The pure, total core: folds one <see cref="AppEvent"/> into the next
/// <see cref="AppState"/>. No IO, no clock, no <see cref="System.Threading.Tasks.Task"/>.
/// Exhaustively unit-testable; the single place application state is ever computed.
/// </summary>
public static class AppReducer
{
    /// <summary>Applies <paramref name="e"/> to <paramref name="state"/>, returning the next state.</summary>
    public static AppState Reduce(AppState state, AppEvent e) => e switch
    {
        AppEvent.TargetDetected ev => Detect(state, ev),

        AppEvent.TargetRemoved ev => Remove(state, ev.Id),

        AppEvent.TargetIdentified ev => state.WithTarget(ev.Id,
            t => t with { Identity = ev.Identity, Stage = FlashStage.Ready }),

        AppEvent.SelectionChanged ev => Select(state, ev.Id),

        AppEvent.FirmwareLoaded ev => state with { Firmware = ev.Firmware, FirmwareError = null },

        AppEvent.FirmwareLoadFailed ev => state with { Firmware = null, FirmwareError = ev.Message },

        // Surfaces on the same error line the front-ends already render, but leaves the image
        // alone: the arm failed, the firmware did not.
        AppEvent.AutoflashArmFailed ev => state with { FirmwareError = ev.Message, Autoflash = null },

        // App-mode reboot lifecycle, ahead of the flash. Entering clears any prior error/progress
        // (a fresh attempt); WaitingForBootloader just advances the stage.
        AppEvent.EnteringBootloader ev => state.WithTarget(ev.Id,
            t => t with { Stage = FlashStage.Entering, Percent = 0, Message = null, LastError = null }),

        AppEvent.WaitingForBootloader ev => state.WithTarget(ev.Id,
            t => t with { Stage = FlashStage.WaitingForBootloader }),

        AppEvent.FlashStarted ev => state.WithTarget(ev.Id,
            t => t with { Stage = FlashStage.Writing, Percent = 0, Message = null, LastError = null }),

        AppEvent.FlashProgressed ev => state.WithTarget(ev.Id,
            t => t with { Stage = MapStage(ev.Progress.Phase, t.Stage), Percent = ev.Progress.Percent, Message = ev.Progress.Message }),

        AppEvent.FlashFinished ev => state.WithTarget(ev.Id, t => Finish(t, ev.Result)),

        // A surfaced error (e.g. a precondition skip) sets LastError but does NOT force
        // the flash stage to Failed — only a started-then-failed flash (FlashFinished) does.
        AppEvent.OperationFailed ev => state.WithTarget(ev.Id,
            t => t with { LastError = ev.Message }),

        // A repeating fixture cannot attribute its flashes to distinct boards: silence cannot tell
        // a board that left from one that reset while seated. The tally carries that so a front-end
        // words the summary as flashes rather than boards.
        AppEvent.AutoflashArmed ev => state with
        {
            Autoflash = ev.Config,
            AutoflashTally = AutoflashTally.Empty with { CountsDistinctBoards = ev.Config.Repeat == RepeatMode.None },
        },

        AppEvent.AutoflashDisarmed => state with { Autoflash = null },

        AppEvent.AutoflashOutcome ev => state with { AutoflashTally = state.AutoflashTally.With(ev.Kind, ev.Id, ev.Detail, ev.Label) },

        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unhandled AppEvent."),
    };

    /// <summary>Convenience: folds a sequence of events left-to-right.</summary>
    public static AppState ReduceAll(AppState state, params AppEvent[] events)
    {
        foreach (var e in events) state = Reduce(state, e);
        return state;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static AppState Detect(AppState s, AppEvent.TargetDetected ev)
    {
        int idx = s.IndexOf(ev.Id);
        AppState next = idx < 0
            ? s with { Targets = s.Targets.Add(new FlashTargetView(ev.Id, ev.DisplayName, ev.ProviderName, ev.Identification, ev.Mode, Bridge: ev.Bridge, PortName: ev.PortName)) }
            : s with { Targets = s.Targets.SetItem(idx, s.Targets[idx] with { DisplayName = ev.DisplayName, ProviderName = ev.ProviderName, Identification = ev.Identification, Mode = ev.Mode, Bridge = ev.Bridge, PortName = ev.PortName }) };

        // Convenience: focus the first target when nothing is selected yet.
        return next.SelectedTargetId is null ? next with { SelectedTargetId = ev.Id } : next;
    }

    private static AppState Remove(AppState s, DeviceId id)
    {
        var targets = s.Targets.RemoveAll(t => t.Id == id);
        if (s.SelectedTargetId != id)
            return s with { Targets = targets };

        // The (DeviceId?) cast is load-bearing, not noise: without it the conditional's
        // natural type resolves through DeviceId's implicit conversion to string, and the
        // null arm then round-trips back string -> DeviceId, which THROWS at runtime while
        // compiling clean. See issue #231.
        DeviceId? selection = targets.Length > 0 ? targets[0].Id : (DeviceId?)null;
        return s with { Targets = targets, SelectedTargetId = selection };
    }

    private static AppState Select(AppState s, DeviceId? id)
    {
        if (id is not { } selected) return s with { SelectedTargetId = null };
        return s.IndexOf(selected) < 0 ? s : s with { SelectedTargetId = selected }; // ignore unknown ids
    }

    private static FlashStage MapStage(FlashPhase phase, FlashStage current) => phase switch
    {
        FlashPhase.Erasing => FlashStage.Erasing,
        FlashPhase.Writing => FlashStage.Writing,
        FlashPhase.Verifying => FlashStage.Verifying,
        _ => current,
    };

    private static FlashTargetView Finish(FlashTargetView t, FlashResult r) => r.Success
        ? t with { Stage = FlashStage.Flashed, Percent = 100, Message = r.Describe(), LastError = null }
        : t with { Stage = FlashStage.Failed, Message = r.Describe(), LastError = r.Error };
}
