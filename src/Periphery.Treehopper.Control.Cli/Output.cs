// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Linq;
using System.Text.Json;
using Periphery.Treehopper.Control;

namespace Periphery.Treehopper.Control.Cli;

/// <summary>Renders <see cref="AppState"/> / boards as SSH-friendly text or AOT-clean <c>--json</c>.</summary>
internal static class Output
{
    public static void BoardList(AppState state, bool json)
    {
        if (json)
        {
            var dto = new BoardListDto(state.FirmwareTarget, state.Boards.Select(Summary).ToArray());
            Console.WriteLine(JsonSerializer.Serialize(dto, CliJson.Default.BoardListDto));
            return;
        }

        Console.WriteLine($"{state.Boards.Length} Treehopper board(s)"
            + (state.FirmwareTarget is int t ? $"  (firmware target {FirmwareVersion.Describe(t)})" : "") + ":");
        foreach (var b in state.Boards)
            Console.WriteLine($"  {b.Label,-14} {VersionText(b.Version),-16} {StatusText(b.Firmware),-18} [{b.Connection}]");
    }

    public static void BoardDetail(BoardView b, bool json)
    {
        if (json) { Console.WriteLine(JsonSerializer.Serialize(Detail(b), CliJson.Default.BoardDetailDto)); return; }

        Console.WriteLine($"{b.Label}  {VersionText(b.Version)}  {StatusText(b.Firmware)}  [{b.Connection}]");
        if (b.LastError is not null) Console.WriteLine($"  ! {b.LastError}");

        Console.WriteLine("  Pin  Mode             Level  ADC");
        foreach (var p in b.Pins)
            Console.WriteLine($"  {p.Number,3}  {p.Mode,-15}  {(p.High ? "HIGH" : "low"),-5}  "
                + (p.Mode == PinMode.AnalogInput ? p.Adc.ToString() : "-"));

        if (b.I2cResponders is { } r)
            Console.WriteLine($"  I2C: {(r.Length == 0 ? "(none)" : string.Join(" ", r.Select(x => $"0x{x:X2}")))}");
    }

    public static string VersionText(int? code) => code is int v ? FirmwareVersion.Describe(v) : "v?";

    public static string StatusText(FirmwareView f) => f.Status switch
    {
        FirmwareStatus.Updating => $"updating {f.Percent}%",
        FirmwareStatus.Failed => $"FAILED: {f.Message}",
        FirmwareStatus.UpdateAvailable => "update available",
        FirmwareStatus.UpToDate => "up to date",
        FirmwareStatus.Updated => "updated",
        _ => "version unknown",
    };

    public static BoardListDto EmptyList() => new(null, Array.Empty<BoardSummaryDto>());

    private static BoardSummaryDto Summary(BoardView b) => new(
        b.Label, b.Id, b.Serial, b.Name, b.Version,
        b.Version is int v ? (v / 100.0).ToString("0.00") : null,
        b.Connection.ToString(),
        b.Firmware.Status.ToString(), b.Firmware.Percent, b.Firmware.Message, b.LastError);

    private static BoardDetailDto Detail(BoardView b) => new(
        b.Label, b.Id, b.Serial, b.Version, b.Connection.ToString(),
        b.Firmware.Status.ToString(), b.LastError,
        b.I2cResponders?.Select(x => $"0x{x:X2}").ToArray(),
        b.Pins.Select(p => new PinDto(p.Number, p.Mode.ToString(), p.High, p.Adc)).ToArray());
}
