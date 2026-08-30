// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader;

/// <summary>
/// The outcome of <see cref="BootloaderEntryOrchestrator.RunAsync{TResult}"/>: the value the flash
/// callback returned, plus whether the application device re-appeared afterwards.
/// </summary>
/// <param name="FlashResult">Whatever the caller's flash callback returned (e.g. an EFM8 upload result).</param>
/// <param name="ApplicationReturned">
/// <c>true</c> if an application device matching <see cref="BootloaderEntryOptions.ApplicationFilter"/>
/// re-appeared within <see cref="BootloaderEntryOptions.ApplicationTimeout"/>. <c>false</c> if no
/// application filter was supplied, if the wait was skipped, or if the device did not return in time
/// (which does not by itself mean the flash failed — the device may simply be slow to re-enumerate).
/// </param>
/// <param name="ApplicationDevice">
/// The re-appeared application device's fresh discovery snapshot, or <c>null</c> under the same
/// conditions as <paramref name="ApplicationReturned"/> being <c>false</c>. Callers that need to act
/// on the device again afterward (e.g. <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/>
/// re-entering the bootloader a second time) must use this snapshot, not the pre-flash one passed
/// into <see cref="BootloaderEntryOrchestrator.RunAsync{TResult}"/> — re-enumeration can change the
/// device id's case (periphery #231), and only a snapshot the orchestrator itself just correlated is
/// proof of which physical device it is.
/// </param>
public sealed record BootloaderEntryResult<TResult>(TResult FlashResult, bool ApplicationReturned, DeviceInfo? ApplicationDevice = null);
