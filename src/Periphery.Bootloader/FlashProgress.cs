// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader;

/// <summary>A flash progress tick, reported via <see cref="System.IProgress{T}"/> during a flash.</summary>
public readonly record struct FlashProgress(FlashPhase Phase, long BytesDone, long BytesTotal, string? Message = null)
{
    /// <summary>Completion of the current phase, 0-100.</summary>
    public int Percent => BytesTotal <= 0 ? 0 : (int)(100L * BytesDone / BytesTotal);
}

/// <summary>The phase a flash is in.</summary>
public enum FlashPhase
{
    Connecting,
    Identifying,
    Erasing,
    Writing,
    Verifying,
    Leaving,
    Done,
}
