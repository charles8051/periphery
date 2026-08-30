// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.FlashAnything;

/// <summary>Aggregate outcome of a fleet flash (<see cref="FlashAnythingService.FlashAllAsync"/>).</summary>
public readonly record struct FleetFlashSummary(int Succeeded, int Failed, int Skipped)
{
    /// <summary>Total targets considered.</summary>
    public int Total => Succeeded + Failed + Skipped;

    /// <summary>A one-line summary for logs and UIs.</summary>
    public string Describe() => $"{Succeeded} ok, {Failed} failed, {Skipped} skipped (of {Total})";
}
