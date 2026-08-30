// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Treehopper;
using Periphery.Treehopper.Control;

namespace Periphery.Treehopper.Control.Cli;

/// <summary>Command handlers. Each drives the shared <see cref="TreehopperControlService"/>.</summary>
internal static class Commands
{
    public static async Task<int> ListAsync(Parsed p, CancellationToken ct)
    {
        int? target = FirmwareSource.ResolveTargetVersion(p.FilePath, p.TargetVersion);
        await using var svc = new TreehopperControlService(new TreehopperControlOptions { FirmwareTargetVersion = target });
        await svc.StartAsync(ct).ConfigureAwait(false);

        var state = svc.State;
        if (state.Boards.Length == 0)
        {
            if (p.Json) Console.WriteLine(JsonSerializer.Serialize(Output.EmptyList(), CliJson.Default.BoardListDto));
            else Console.WriteLine("No Treehopper boards are connected.");
            return ExitCodes.NoBoards;
        }
        Output.BoardList(state, p.Json);
        return ExitCodes.Success;
    }

    public static async Task<int> WatchAsync(Parsed p, CancellationToken ct)
    {
        await using var svc = new TreehopperControlService();
        await svc.StartAsync(ct).ConfigureAwait(false);

        var board = Resolve(svc.State, p.Selector);
        if (board is null) return NotFound(p.Selector);
        string id = board.Id;

        await svc.DispatchAsync(new AppIntent.SelectBoard(id), ct).ConfigureAwait(false);
        await svc.SetLiveStreamingAsync(true, ct).ConfigureAwait(false);

        void Render()
        {
            var b = svc.State.Find(id);
            if (b is null) return;
            if (!p.Json && !Console.IsOutputRedirected) { try { Console.Clear(); } catch { } }
            Output.BoardDetail(b, p.Json);
        }

        EventHandler<AppState> handler = (_, _) => Render();
        svc.StateChanged += handler;
        Render();
        try { await Task.Delay(p.Seconds is int s ? s * 1000 : Timeout.Infinite, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        svc.StateChanged -= handler;
        return ExitCodes.Success;
    }

    public static async Task<int> PinAsync(Parsed p, CancellationToken ct)
    {
        await using var svc = new TreehopperControlService();
        await svc.StartAsync(ct).ConfigureAwait(false);

        var board = Resolve(svc.State, p.Selector);
        if (board is null) return NotFound(p.Selector);
        string id = board.Id;

        AppIntent intent = p.PinAction switch
        {
            "high" => new AppIntent.DriveOutput(id, p.Pin, true),
            "low" => new AppIntent.DriveOutput(id, p.Pin, false),
            "output" => new AppIntent.SetPinMode(id, p.Pin, PinMode.PushPullOutput),
            "input" => new AppIntent.SetPinMode(id, p.Pin, PinMode.DigitalInput),
            "analog" => new AppIntent.SetPinMode(id, p.Pin, PinMode.AnalogInput),
            _ => throw new InvalidOperationException($"Unhandled pin action '{p.PinAction}'."),
        };

        await svc.DispatchAsync(new AppIntent.SelectBoard(id), ct).ConfigureAwait(false);
        await svc.DispatchAsync(intent, ct).ConfigureAwait(false);
        // Let the report stream reflect the new level before we render.
        try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }

        var b = svc.State.Find(id) ?? board;
        Output.BoardDetail(b, p.Json);
        return b.LastError is null ? ExitCodes.Success : ExitCodes.FlashFailed;
    }

    public static async Task<int> I2cAsync(Parsed p, CancellationToken ct)
    {
        await using var svc = new TreehopperControlService();
        await svc.StartAsync(ct).ConfigureAwait(false);

        var board = Resolve(svc.State, p.Selector);
        if (board is null) return NotFound(p.Selector);
        string id = board.Id;

        await svc.DispatchAsync(new AppIntent.SelectBoard(id), ct).ConfigureAwait(false);
        await svc.DispatchAsync(new AppIntent.ScanI2c(id), ct).ConfigureAwait(false);

        var b = svc.State.Find(id) ?? board;
        Output.BoardDetail(b, p.Json);
        return b.LastError is null ? ExitCodes.Success : ExitCodes.FlashFailed;
    }

    public static async Task<int> FirmwareAsync(Parsed p, bool all, CancellationToken ct)
    {
        int? target = FirmwareSource.ResolveTargetVersion(p.FilePath, p.TargetVersion);
        var (bytes, origin, error) = FirmwareSource.ResolveImage(p.FilePath);
        if (error is not null) { Console.Error.WriteLine(error); return ExitCodes.NoImage; }

        if (all && target is null && !p.Force && p.Yes)
        {
            Console.Error.WriteLine(
                "Refusing to flash all boards without a target version. "
                + "Pass --target-version <code> to gate by version, or --force to flash every board.");
            return ExitCodes.Usage;
        }

        await using var svc = new TreehopperControlService(new TreehopperControlOptions
        {
            FirmwareImage = bytes,
            FirmwareTargetVersion = target,
        });
        await svc.StartAsync(ct).ConfigureAwait(false);

        IReadOnlyList<BoardView> boards;
        if (all)
        {
            boards = svc.State.Boards;
        }
        else
        {
            var one = Resolve(svc.State, p.Selector);
            if (one is null) return NotFound(p.Selector);
            boards = new[] { one };
        }

        if (boards.Count == 0)
        {
            Console.WriteLine("No Treehopper boards are connected.");
            return ExitCodes.NoBoards;
        }

        var plan = boards.Where(b => ShouldFlash(b, p.Force, target)).ToList();

        if (!p.Json)
        {
            Console.WriteLine($"Firmware: {origin}. Target: {(target is int t ? FirmwareVersion.Describe(t) : "none")}. "
                + (p.Yes ? "Mode: APPLY." : "Mode: dry run (pass --yes to flash)."));
            foreach (var b in boards)
                Console.WriteLine($"  {b.Label,-14} {Output.VersionText(b.Version)}  "
                    + (plan.Contains(b) ? "-> will flash" : $"skip ({Output.StatusText(b.Firmware)})"));
        }

        if (!p.Yes)
        {
            if (p.Json) Console.WriteLine(JsonSerializer.Serialize(
                new FirmwarePlanDto(true, origin, target, plan.Select(b => b.Label).ToArray()),
                CliJson.Default.FirmwarePlanDto));
            return ExitCodes.Success;
        }

        foreach (var b in plan)
        {
            if (!p.Json) Console.WriteLine($"Flashing {b.Label}...");
            await svc.DispatchAsync(new AppIntent.UpdateFirmware(b.Id), ct).ConfigureAwait(false);
            if (!p.Json)
            {
                var nb = svc.State.Find(b.Id);
                Console.WriteLine($"  {b.Label}: {Output.StatusText(nb?.Firmware ?? b.Firmware)}");
            }
        }

        var final = svc.State;
        int failed = plan.Count(b => final.Find(b.Id)?.Firmware.Status == FirmwareStatus.Failed);

        if (p.Json) Console.WriteLine(JsonSerializer.Serialize(
            new FirmwareResultDto(origin, target, plan.Count, plan.Count - failed, failed, boards.Count - plan.Count),
            CliJson.Default.FirmwareResultDto));
        else Console.WriteLine($"Summary: {plan.Count - failed} updated, {failed} failed, {boards.Count - plan.Count} skipped.");

        return failed > 0 ? ExitCodes.FlashFailed : ExitCodes.Success;
    }

    private static bool ShouldFlash(BoardView b, bool force, int? target) =>
        force || target is null || b.Firmware.Status == FirmwareStatus.UpdateAvailable;

    private static BoardView? Resolve(AppState state, string? selector)
    {
        if (selector is null) return state.Boards.Length > 0 ? state.Boards[0] : null;
        return state.Boards.FirstOrDefault(b =>
            string.Equals(b.Serial, selector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(b.Id, selector, StringComparison.OrdinalIgnoreCase));
    }

    private static int NotFound(string? selector)
    {
        Console.Error.WriteLine(selector is null
            ? "No Treehopper boards are connected."
            : $"No Treehopper board matching '{selector}' is connected.");
        return ExitCodes.NoBoards;
    }
}
