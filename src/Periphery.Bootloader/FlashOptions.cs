// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader;

/// <summary>How a flash should be performed. Immutable; reuse <see cref="Default"/> or <c>with</c>-edit it.</summary>
public sealed record FlashOptions
{
    /// <summary>Read back and compare after writing (where the protocol supports upload/verify).</summary>
    public bool Verify { get; init; } = true;

    /// <summary>How to erase before writing.</summary>
    public EraseMode Erase { get; init; } = EraseMode.Auto;

    /// <summary>Leave the bootloader and start the application after a successful flash.</summary>
    public bool LeaveAfterFlash { get; init; } = true;

    /// <summary>The default options: verify, auto-erase, leave after flash.</summary>
    public static FlashOptions Default { get; } = new();
}

/// <summary>Erase strategy before writing.</summary>
public enum EraseMode
{
    /// <summary>Let the programmer choose (mass erase unless it can do better).</summary>
    Auto,
    /// <summary>Mass-erase the whole flash.</summary>
    Mass,
    /// <summary>Erase only the pages/sectors the image touches.</summary>
    PerPage,
    /// <summary>Do not erase (caller guarantees the target is already erased).</summary>
    None,
}
