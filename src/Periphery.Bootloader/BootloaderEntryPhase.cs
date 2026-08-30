// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader;

/// <summary>
/// The app-mode phases <see cref="BootloaderEntryOrchestrator"/> reports before the flash itself
/// begins — the device-specific reboot/wait that precedes the reusable flash. A front-end renders
/// these (and the flash's own <see cref="FlashProgress"/>) as the target's lifecycle (ADR-0063 DEC-004).
/// </summary>
public enum BootloaderEntryPhase
{
    /// <summary>Sending the wake command to the application device (<see cref="IBootloaderEntry.EnterAsync"/>).</summary>
    Entering,

    /// <summary>The wake command has been sent; waiting for the device to re-enumerate as its bootloader.</summary>
    WaitingForBootloader,

    /// <summary>
    /// The mode switch failed and <see cref="BootloaderEntryOptions.Recovery"/> is resetting
    /// the device before retrying (ADR-0076). Reported once per reset rung, so a front-end
    /// can show that the updater is escalating rather than hung. Only ever reported when
    /// recovery is configured.
    /// </summary>
    Recovering,
}
