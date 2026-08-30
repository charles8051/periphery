// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Firmware;

namespace Periphery.Bootloader;

/// <summary>
/// A live session with a device's bootloader: identify it, write an image, leave. One
/// per <c>(family, transport)</c> flasher package implements this
/// (<c>Periphery.Bootloader.Stm32.Usb</c>, <c>.Efm8.Usb</c>, ...).
/// </summary>
/// <remarks>
/// This is the imperative-shell boundary (ADR-0052): the implementation owns the
/// transport handle, the protocol poll loop, and all timing. The pure protocol core
/// (encode/decode/plan) lives beneath it.
/// </remarks>
public interface IFirmwareProgrammer : IAsyncDisposable
{
    /// <summary>The discovery snapshot this programmer was opened from.</summary>
    DeviceInfo Device { get; }

    /// <summary>
    /// Reads the target's identity and capabilities (family, chip, bootloader version,
    /// transfer size, memory map, discovered command set).
    /// </summary>
    Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default);

    /// <summary>
    /// The firmware formats this programmer can flash — its safety gate. A byte-writing flasher
    /// (STM32 DFU) accepts the Kind-1 memory-image formats; a packaged-blob flasher (EFM8) accepts
    /// its native blob format. <see cref="FlashAsync"/> refuses a <see cref="FirmwarePayload"/>
    /// whose <see cref="FirmwarePayload.Format"/> is not listed, before any byte is written.
    /// </summary>
    ImmutableArray<FirmwareFormat> AcceptedFormats { get; }

    /// <summary>
    /// Writes <paramref name="payload"/> to the device per <paramref name="options"/>, reporting
    /// progress through <paramref name="progress"/>. The payload is either a Kind-1 memory image
    /// (addressed segments) or a Kind-2 packaged blob (consumed as-is); a programmer flashes the
    /// kind(s) in <see cref="AcceptedFormats"/> and fails fast on any other. When
    /// <see cref="FlashOptions.LeaveAfterFlash"/> is set, a successful write also leaves the
    /// bootloader as its final step (the device resets and runs the app), so callers must
    /// <b>not</b> additionally call <see cref="LeaveAsync"/> afterwards.
    /// </summary>
    Task<FlashResult> FlashAsync(
        FirmwarePayload payload,
        FlashOptions options,
        IProgress<FlashProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Leaves the bootloader and starts the application as a <i>standalone</i> action (leaving
    /// without flashing), where the protocol supports it (e.g. DFU manifest + reset); a no-op for
    /// protocols that reset implicitly. The flash path already leaves via <see cref="FlashAsync"/>
    /// when <see cref="FlashOptions.LeaveAfterFlash"/> is set — do not call this in addition after
    /// a flash, as the device has already reset and a second leave hits a detached device.
    /// </summary>
    Task LeaveAsync(CancellationToken ct = default);
}
