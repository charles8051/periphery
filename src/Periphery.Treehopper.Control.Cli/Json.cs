// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery.Treehopper.Control.Cli;

// AOT-clean JSON: concrete DTOs + a source-generated serializer context (no reflection).

internal sealed record BoardSummaryDto(
    string Label, string Id, string? Serial, string? Name,
    int? Version, string? VersionText, string Connection,
    string FirmwareStatus, int? FirmwarePercent, string? FirmwareMessage, string? LastError);

internal sealed record PinDto(int Number, string Mode, bool High, int Adc);

internal sealed record BoardDetailDto(
    string Label, string Id, string? Serial, int? Version, string Connection,
    string FirmwareStatus, string? LastError, string[]? I2cResponders, PinDto[] Pins);

internal sealed record BoardListDto(int? Target, BoardSummaryDto[] Boards);

internal sealed record FirmwarePlanDto(bool DryRun, string Firmware, int? Target, string[] WouldFlash);

internal sealed record FirmwareResultDto(
    string Firmware, int? Target, int Planned, int Updated, int Failed, int Skipped);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BoardListDto))]
[JsonSerializable(typeof(BoardDetailDto))]
[JsonSerializable(typeof(FirmwarePlanDto))]
[JsonSerializable(typeof(FirmwareResultDto))]
internal partial class CliJson : JsonSerializerContext;
