// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Bootloader.Efm8.Usb;

namespace Periphery.Treehopper.Firmware;

/// <summary>
/// The outcome of a read-only <see cref="TreehopperFirmwareUpdate.VerifyFromFileAsync(DeviceInfo, string, TreehopperReflashOptions?, System.Threading.CancellationToken)"/>
/// check: whether the board's <b>current</b> flash content matches the given image, plus whether the
/// application re-enumerated afterward. Nothing about this outcome is a flash result — no Erase or
/// Write record exists in a verify-only stream, so the board's firmware is unchanged either way.
/// </summary>
/// <param name="Upload">The record-replay result of the verify-only stream.</param>
/// <param name="ApplicationReturned">
/// <c>true</c> if the application device re-enumerated within the timeout after the final RunApp
/// record. See <see cref="TreehopperReflashResult.ApplicationReturned"/> for the same caveat: a
/// <c>false</c> here does not by itself mean anything is wrong.
/// </param>
public sealed record TreehopperVerifyResult(Efm8UploadResult Upload, bool ApplicationReturned)
{
    /// <summary>
    /// <c>true</c> when every record — including the Verify record(s) — was acknowledged, i.e. the
    /// board's current flash content matches the image this check was built from, byte for byte, per
    /// the bootloader's own CRC-16/XMODEM check.
    /// </summary>
    public bool Matches => Upload.Success;

    /// <summary>
    /// <c>true</c> when the upload failed specifically because a Verify record was rejected — a
    /// genuine content mismatch, as opposed to some other failure (a stalled bootloader, an
    /// unexpected reply to Setup, etc.) that says nothing about whether the content matches.
    /// </summary>
    public bool ContentMismatch => !Upload.Success && Upload.FailedCommand == Efm8BootRecordGenerator.VerifyCommand;
}
