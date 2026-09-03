// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// An immutable projection of one flashable target — the row both front-ends render.
/// Produced only by <see cref="AppReducer"/>.
/// </summary>
public sealed record FlashTargetView(
    // Typed DeviceId, not string: the same device re-enumerates with different casing
    // in its instance id (issue #231), and DeviceId compares OrdinalIgnoreCase.
    DeviceId Id,
    string DisplayName,
    string ProviderName,
    IdentificationMode Identification = IdentificationMode.Passive,
    DeviceMode Mode = DeviceMode.Bootloader,
    DeviceIdentity? Identity = null,
    FlashStage Stage = FlashStage.Detected,
    int Percent = 0,
    string? Message = null,
    string? LastError = null,
    // Null for passive families, which need no binding, and for a probe target whose bridge could
    // not be identified — which AutoflashPolicy treats as ineligible rather than as a match.
    BridgeIdentity? Bridge = null,
    // The port a probe target sits on. Null for a passive one, which is named by what it said it is
    // rather than by where it is plugged.
    SerialPortName? PortName = null)
{
    /// <summary>
    /// True if flashing this target first reboots it into a bootloader (an application-mode device an
    /// <c>IBootloaderEntry</c> handles), rather than flashing it directly. The front-ends surface this
    /// (e.g. "Treehopper (application) — reboots to flash"); for these, <see cref="ProviderName"/> is
    /// the entry's family name.
    /// </summary>
    public bool RebootsToFlash => Mode == DeviceMode.Application;

    /// <summary>
    /// How this target should be named to an operator.
    /// <para>
    /// A probe target is a position on a bench, not a device that identified itself: what is behind
    /// the bridge is only knowable by asking, and every board of one part number answers alike. So
    /// it reads as its fixture — the port, and the chip if a probe has established one — rather than
    /// as the bridge's USB instance id, which is both unreadable and a claim about the wrong thing.
    /// A passive target keeps its own name, because it really did say what it is.
    /// </para>
    /// </summary>
    public string OperatorLabel
    {
        get
        {
            if (Identification == IdentificationMode.Passive)
                return DisplayName;

            string port = PortName?.Value ?? DisplayName;
            return Identity?.Chip is { } chip ? $"{port} (fixture, {chip})" : $"{port} (fixture)";
        }
    }

}

/// <summary>
/// Whether a discovered target is already in its bootloader, or running its application firmware and
/// must be rebooted into the bootloader before flashing (ADR-0063 DEC-004).
/// </summary>
public enum DeviceMode
{
    /// <summary>Already a bootloader (e.g. an STM32 in DFU, an EFM8 HID bootloader). Flashed directly.</summary>
    Bootloader,

    /// <summary>Running application firmware; an <c>IBootloaderEntry</c> reboots it into its bootloader first.</summary>
    Application,
}

/// <summary>Where a target is in the detect -> (enter -> wait) -> identify -> flash lifecycle.</summary>
public enum FlashStage
{
    Detected,
    Identifying,
    Ready,
    /// <summary>App-mode: sending the wake command to reboot into the bootloader.</summary>
    Entering,
    /// <summary>App-mode: the device is rebooting; waiting for its bootloader to re-enumerate.</summary>
    WaitingForBootloader,
    Erasing,
    Writing,
    Verifying,
    Flashed,
    Failed,
}
