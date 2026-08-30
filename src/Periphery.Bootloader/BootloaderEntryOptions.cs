// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader;

/// <summary>
/// Tunables for <see cref="BootloaderEntryOrchestrator.RunAsync{TResult}"/>. All have defaults; the
/// only field a caller usually sets is <see cref="ApplicationFilter"/> (to wait for the app to
/// return after flashing).
/// </summary>
public sealed record BootloaderEntryOptions
{
    /// <summary>
    /// How long to wait for the device to re-enumerate as <see cref="IBootloaderEntry.ExpectedBootloader"/>
    /// after <see cref="IBootloaderEntry.EnterAsync"/>. Default 15 seconds.
    /// </summary>
    public TimeSpan BootloaderTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How the orchestrator correlates the re-enumerated bootloader with the application it rebooted
    /// (ADR-0063 DEC-005). Default <see cref="DeviceCorrelationMode.FirstAppearance"/> (debounce),
    /// correct for no-serial families such as the EFM8 HID bootloader.
    /// </summary>
    public DeviceCorrelationMode Correlation { get; init; } = DeviceCorrelationMode.FirstAppearance;

    /// <summary>
    /// When set, after a successful flash the orchestrator waits for a device matching this filter
    /// (the application's own identity) to re-appear, and reports it via
    /// <see cref="BootloaderEntryResult{TResult}.ApplicationReturned"/>. <c>null</c> (the default)
    /// skips the wait. This wait accepts a pre-existing match — it is a liveness check, not a
    /// re-enumeration correlation.
    /// </summary>
    public DeviceFilter? ApplicationFilter { get; init; }

    /// <summary>How long to wait for the application to return; honoured only when <see cref="ApplicationFilter"/> is set. Default 15 seconds.</summary>
    public TimeSpan ApplicationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Opt-in recovery for a failed mode switch (ADR-0076): reset the device and retry
    /// instead of failing the update. <see langword="null"/> (the default) preserves the
    /// original behaviour exactly — the first failed entry fails the run.
    /// <para>
    /// Worth setting whenever the caller can tolerate a device-disrupting reset, because
    /// the failure it recovers from is otherwise terminal: the mode-switch command rides
    /// the device's normal data path, so a wedged data path makes the device unflashable
    /// by the very tool meant to fix it.
    /// </para>
    /// </summary>
    public BootloaderEntryRecovery? Recovery { get; init; }
}
