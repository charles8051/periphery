// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Treehopper.Firmware;

/// <summary>
/// Tunables for <see cref="TreehopperFirmwareUpdate.ReflashAsync(TreehopperBoard, System.IO.Stream, Periphery.Bootloader.Efm8.Usb.Efm8FlashConfirmation, TreehopperReflashOptions?, System.IProgress{Periphery.Bootloader.Efm8.Usb.Efm8UploadProgress}?, System.Threading.CancellationToken)"/>.
/// All have sane defaults; pass <c>null</c> to accept them.
/// </summary>
/// <param name="BootloaderTimeout">
/// How long to wait for the HID bootloader (<c>0x10C4:0xEAC9</c>) to enumerate after
/// the board reboots into it. Default 15 seconds.
/// </param>
/// <param name="ApplicationTimeout">
/// How long to wait for the application (<c>0x10C4:0x8A7E</c>) to re-enumerate after
/// the final RunApp record resets the device. Ignored when
/// <paramref name="WaitForApplication"/> is <c>false</c>. Default 15 seconds.
/// </param>
/// <param name="PollInterval">
/// How often to re-enumerate while waiting across the two USB re-enumerations.
/// Default 250 ms.
/// </param>
/// <param name="WaitForApplication">
/// After a successful upload, poll for the application to come back and report it in
/// <see cref="TreehopperReflashResult.ApplicationReturned"/>. Default <c>true</c>.
/// </param>
public sealed record TreehopperReflashOptions(
    TimeSpan? BootloaderTimeout = null,
    TimeSpan? ApplicationTimeout = null,
    TimeSpan? PollInterval = null,
    bool WaitForApplication = true)
{
    internal TimeSpan EffectiveBootloaderTimeout => BootloaderTimeout ?? TimeSpan.FromSeconds(15);
    internal TimeSpan EffectiveApplicationTimeout => ApplicationTimeout ?? TimeSpan.FromSeconds(15);
    internal TimeSpan EffectivePollInterval => PollInterval ?? TimeSpan.FromMilliseconds(250);
}
