// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// Something that happened, folded into <see cref="AppState"/> by
/// <see cref="AppReducer.Reduce"/>. A closed union — the <c>private protected</c>
/// constructor keeps the reducer's switch total. Produced by the app service from
/// discovery, the bootloader contract, and applied intents.
/// </summary>
public abstract record AppEvent
{
    private protected AppEvent() { }

    /// <summary>A flashable target appeared (or its display info was refreshed).</summary>
    public sealed record TargetDetected(
        DeviceId Id, string DisplayName, string ProviderName,
        IdentificationMode Identification = IdentificationMode.Passive,
        DeviceMode Mode = DeviceMode.Bootloader,
        // The USB-serial bridge this target was found behind, for probe families. Null for passive
        // ones, which identify themselves, and for a bridge that could not be identified — which
        // AutoflashPolicy treats as ineligible rather than as a match.
        BridgeIdentity? Bridge = null) : AppEvent;

    /// <summary>A target disappeared.</summary>
    public sealed record TargetRemoved(DeviceId Id) : AppEvent;

    /// <summary>A target's identity/capabilities were read.</summary>
    public sealed record TargetIdentified(DeviceId Id, DeviceIdentity Identity) : AppEvent;

    /// <summary>The focused target changed (null = nothing selected).</summary>
    public sealed record SelectionChanged(DeviceId? Id) : AppEvent;

    /// <summary>A firmware image was loaded for flashing.</summary>
    public sealed record FirmwareLoaded(FirmwareSelection Firmware) : AppEvent;

    /// <summary>A firmware image failed to load (bad format, unreadable, or unsupported).</summary>
    public sealed record FirmwareLoadFailed(string Message) : AppEvent;

    /// <summary>An application-mode target is being rebooted into its bootloader (the wake command is sent).</summary>
    public sealed record EnteringBootloader(DeviceId Id) : AppEvent;

    /// <summary>An application-mode target has been rebooted; waiting for its bootloader to re-enumerate.</summary>
    public sealed record WaitingForBootloader(DeviceId Id) : AppEvent;

    /// <summary>A flash began on a target.</summary>
    public sealed record FlashStarted(DeviceId Id) : AppEvent;

    /// <summary>A flash progress tick from the bootloader contract.</summary>
    public sealed record FlashProgressed(DeviceId Id, FlashProgress Progress) : AppEvent;

    /// <summary>A flash finished (success or failure carried in the result).</summary>
    public sealed record FlashFinished(DeviceId Id, FlashResult Result) : AppEvent;

    /// <summary>An operation on a target failed; the message is surfaced on the target.</summary>
    public sealed record OperationFailed(DeviceId Id, string Message) : AppEvent;

    /// <summary>Autoflash was armed for a family/provider + options; resets the session tally.</summary>
    public sealed record AutoflashArmed(AutoflashConfig Config) : AppEvent;

    /// <summary>Autoflash was disarmed (no more automatic flashing).</summary>
    public sealed record AutoflashDisarmed : AppEvent;

    /// <summary>A per-device autoflash decision/result, folded into the session tally + audit.</summary>
    public sealed record AutoflashOutcome(DeviceId Id, AutoflashOutcomeKind Kind, string? Detail = null) : AppEvent;
}
