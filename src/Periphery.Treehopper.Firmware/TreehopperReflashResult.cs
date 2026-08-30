// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Bootloader.Efm8.Usb;

namespace Periphery.Treehopper.Firmware;

/// <summary>
/// The outcome of a full Treehopper reflash: the underlying EFM8 upload result plus
/// whether the application re-enumerated afterwards.
/// </summary>
/// <param name="Upload">
/// The record-replay result. <c>Upload.Success</c> is the authoritative
/// flash-succeeded signal.
/// </param>
/// <param name="ApplicationReturned">
/// <c>true</c> if the application device (<c>0x10C4:0x8A7E</c>) re-enumerated within
/// the timeout after the final RunApp record. <c>false</c> if the upload failed, if
/// waiting was disabled, or if the app did not return in time (which does not by
/// itself mean the flash failed — the device may simply be slow to re-enumerate).
/// </param>
public sealed record TreehopperReflashResult(Efm8UploadResult Upload, bool ApplicationReturned)
{
    /// <summary>Convenience: whether the firmware upload itself succeeded.</summary>
    public bool Success => Upload.Success;
}
