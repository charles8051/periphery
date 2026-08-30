// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Firmware;

namespace Periphery.Bootloader;

/// <summary>
/// Puts a device that is running its <em>application</em> firmware into its bootloader, so a
/// flasher can take over. This is the device-specific half of flashing an app-mode device; the
/// reusable half is the <see cref="IBootloaderProvider"/> (or, in slice 1, a flash callback) for
/// whatever bootloader it becomes. One implementation per <c>(application family, transport)</c>:
/// Treehopper HID reboot, Arduino 1200 bps touch, ESP RTS/DTR, STM32 app DFU-detach, ...
/// </summary>
/// <remarks>
/// <para>
/// An entry is <b>not</b> a new flasher — it is a small mode switch composed in front of an
/// existing one (ADR-0063 DEC-001). Entries and flashers are independent and joined by the shared
/// <see cref="BootloaderEntryOrchestrator"/>, so one flasher (e.g. <c>Efm8.Usb</c>) serves every
/// device that re-enumerates as that bootloader.
/// </para>
/// <para>
/// Implementations open the application device with its own SDK, send the wake command, and dispose.
/// They do <b>not</b> poll, gate, or flash — the orchestrator owns the wait, the
/// <see cref="ExpectedBootloader"/> safety gate, and the flash.
/// </para>
/// </remarks>
public interface IBootloaderEntry
{
    /// <summary>The application this enters the bootloader for (e.g. <c>"Treehopper"</c>).</summary>
    string Name { get; }

    /// <summary>True if <paramref name="applicationDevice"/>, in application mode, is one this entry can reboot.</summary>
    bool CanEnter(DeviceInfo applicationDevice);

    /// <summary>
    /// A filter matching the bootloader the device re-enumerates as, so the orchestrator can wait
    /// for and recognize it — and refuse to write to anything else (the safety gate). E.g.
    /// Treehopper → EFM8 HID <c>0x10C4:0xEAC9</c>; STM32 app → DFU <c>0x0483:0xDF11</c>.
    /// </summary>
    DeviceFilter ExpectedBootloader { get; }

    /// <summary>
    /// Commands the device into its bootloader. After this returns the device drops off the bus and
    /// reappears matching <see cref="ExpectedBootloader"/>; the orchestrator owns the wait and the
    /// correlation. Implementations open the application device with its own SDK, send the wake
    /// command, and dispose — they do not poll, gate, or flash.
    /// </summary>
    Task EnterAsync(DeviceInfo applicationDevice, CancellationToken ct);

    /// <summary>
    /// Whether this family can independently confirm a just-flashed payload against a device's
    /// current flash content, from a genuinely separate, later bootloader session (periphery#246: a
    /// flash's own embedded/in-session check is not always proof a write landed). Defaults
    /// <see langword="false"/> — a family whose flasher already verifies in-session (e.g. STM32 DFU
    /// via <see cref="FlashOptions.Verify"/>) should never pay for an extra, unnecessary
    /// reboot-into-bootloader round-trip. When <see langword="true"/>,
    /// <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/> uses
    /// <see cref="VerifyAsync"/> to retry a mismatched flash automatically.
    /// </summary>
    bool CanVerify => false;

    /// <summary>
    /// Independently confirms <paramref name="payload"/> — the same payload just flashed — against
    /// <paramref name="bootloaderDevice"/>'s current content, from a device already re-entered into
    /// its bootloader. Only ever called when <see cref="CanVerify"/> is <see langword="true"/>; the
    /// default throws, matching that default.
    /// </summary>
    Task<bool> VerifyAsync(DeviceInfo bootloaderDevice, FirmwarePayload payload, CancellationToken ct)
        => throw new NotSupportedException($"'{Name}' does not support independent verification.");
}
