// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Treehopper.Control;

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
        AppEvent.BoardDiscovered ev => Discover(state, ev.Board),

        AppEvent.BoardRemoved ev => Remove(state, ev.Id),

        AppEvent.BoardVersionRead ev => state.WithBoard(ev.Id,
            b => (b with { Version = ev.Version }).WithIdleFirmware(state.FirmwareTarget)),

        AppEvent.SelectionChanged ev => Select(state, ev.Id),

        AppEvent.PinModeChanged ev => state.WithBoard(ev.Id, b => SetPinMode(b, ev.Pin, ev.Mode)),

        AppEvent.OutputDriven ev => state.WithBoard(ev.Id, b => DrivePin(b, ev.Pin, ev.High)),

        AppEvent.ReportReceived ev => state.WithBoard(ev.Id, b => ApplyReport(b, ev.Report)),

        AppEvent.FirmwareTargetSet ev => SetTarget(state, ev.Target),

        AppEvent.FirmwareUpdateStarted ev => state.WithBoard(ev.Id,
            b => b with { Firmware = new FirmwareView(FirmwareStatus.Updating, Percent: 0), LastError = null }),

        AppEvent.FirmwareProgressed ev => state.WithBoard(ev.Id,
            b => b with { Firmware = new FirmwareView(FirmwareStatus.Updating, Percent: Percent(ev.RecordsSent, ev.TotalRecords)) }),

        AppEvent.FirmwareUpdateFinished ev => state.WithBoard(ev.Id, b => FinishFirmware(b, ev)),

        AppEvent.I2cScanStarted ev => state.WithBoard(ev.Id, b => b with { I2cScanning = true }),

        AppEvent.I2cScanFinished ev => state.WithBoard(ev.Id,
            b => b with { I2cScanning = false, I2cResponders = ev.Responders }),

        AppEvent.OperationFailed ev => state.WithBoard(ev.Id, b => b with { LastError = ev.Message }),

        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unhandled AppEvent."),
    };

    /// <summary>Convenience: folds a sequence of events left-to-right.</summary>
    public static AppState ReduceAll(AppState state, params AppEvent[] events)
    {
        foreach (var e in events) state = Reduce(state, e);
        return state;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static AppState Discover(AppState s, BoardIdentity id)
    {
        int idx = s.IndexOf(id.Id);
        AppState next = idx < 0
            ? s with { Boards = s.Boards.Add(BoardView.FromIdentity(id).WithIdleFirmware(s.FirmwareTarget)) }
            : s with { Boards = s.Boards.SetItem(idx, MergeIdentity(s.Boards[idx], id).WithIdleFirmware(s.FirmwareTarget)) };

        // Convenience: focus the first board when nothing is selected yet.
        return next.SelectedBoardId is null ? next with { SelectedBoardId = id.Id } : next;
    }

    private static BoardView MergeIdentity(BoardView b, BoardIdentity id) => b with
    {
        Serial = id.Serial ?? b.Serial,
        Name = id.Name ?? b.Name,
        Version = id.Version ?? b.Version,
        Connection = id.Connection,
    };

    private static AppState Remove(AppState s, DeviceId id)
    {
        var boards = s.Boards.RemoveAll(b => b.Id == id);
        if (s.SelectedBoardId != id)
            return s with { Boards = boards };

        // The selected board went away — fall back to the first remaining, or nothing.
        // The (DeviceId?) cast is load-bearing, not noise: without it the conditional's
        // natural type resolves through DeviceId's implicit conversion to string, and the
        // null arm then round-trips back string -> DeviceId, which THROWS at runtime while
        // compiling clean. See issue #231.
        DeviceId? selection = boards.Length > 0 ? boards[0].Id : (DeviceId?)null;
        return s with { Boards = boards, SelectedBoardId = selection };
    }

    private static AppState Select(AppState s, DeviceId? id)
    {
        if (id is not { } selected) return s with { SelectedBoardId = null };
        return s.IndexOf(selected) < 0 ? s : s with { SelectedBoardId = selected }; // ignore unknown ids
    }

    private static BoardView SetPinMode(BoardView b, int pin, PinMode mode)
    {
        if (pin < 0 || pin >= b.Pins.Length) return b;
        return b with { Pins = b.Pins.SetItem(pin, b.Pins[pin] with { Mode = mode }) };
    }

    private static BoardView DrivePin(BoardView b, int pin, bool high)
    {
        if (pin < 0 || pin >= b.Pins.Length) return b;
        return b with { Pins = b.Pins.SetItem(pin, b.Pins[pin] with { Mode = PinMode.PushPullOutput, High = high }) };
    }

    private static BoardView ApplyReport(BoardView b, BoardReport report)
    {
        var builder = b.Pins.ToBuilder();
        int n = Math.Min(b.Pins.Length, report.Pins.Length);
        for (int i = 0; i < n; i++)
        {
            var pin = b.Pins[i];
            // Output levels are host-authoritative — the firmware reports input changes,
            // not host-driven output changes, so don't let a report clobber a driven level.
            bool isOutput = pin.Mode is PinMode.PushPullOutput or PinMode.OpenDrainOutput;
            builder[i] = pin with
            {
                High = isOutput ? pin.High : report.Pins[i].Digital,
                Adc = report.Pins[i].Adc,
            };
        }
        return b with { Pins = builder.ToImmutable() };
    }

    private static AppState SetTarget(AppState s, int? target)
    {
        var builder = s.Boards.ToBuilder();
        for (int i = 0; i < s.Boards.Length; i++)
            builder[i] = s.Boards[i].WithIdleFirmware(target);
        return s with { FirmwareTarget = target, Boards = builder.ToImmutable() };
    }

    private static int Percent(int sent, int total) =>
        total <= 0 ? 100 : (int)(100L * sent / total);

    private static BoardView FinishFirmware(BoardView b, AppEvent.FirmwareUpdateFinished ev)
    {
        if (!ev.Success)
            return b with
            {
                Firmware = new FirmwareView(FirmwareStatus.Failed, Message: ev.Message),
                LastError = ev.Message,
            };

        var withVersion = ev.NewVersion is int v ? b with { Version = v } : b;
        return withVersion with { Firmware = new FirmwareView(FirmwareStatus.Updated) };
    }
}
