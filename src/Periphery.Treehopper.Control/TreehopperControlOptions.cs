// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Treehopper.Control;

/// <summary>Host-supplied configuration for <see cref="TreehopperControlService"/>.</summary>
public sealed record TreehopperControlOptions
{
    /// <summary>
    /// The firmware image (a hex2boot-produced .tfi/.efm8) used by the
    /// <see cref="AppIntent.UpdateFirmware"/> intent. Null disables firmware updates
    /// (the intent reports an error). The front-end resolves this (embedded / --file).
    /// </summary>
    public byte[]? FirmwareImage { get; init; }

    /// <summary>
    /// The target firmware version (raw bcdDevice code) the image installs, used to
    /// derive each board's up-to-date / update-available status. Null = unknown.
    /// </summary>
    public int? FirmwareTargetVersion { get; init; }

    /// <summary>Per-call deadline for the read-only USB version read. Default 3s.</summary>
    public TimeSpan VersionReadTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
