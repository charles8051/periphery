// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Periphery.Bootloader;

namespace Periphery.FlashAnything;

/// <summary>
/// What the user wants to do — emitted by a front-end, executed by
/// <see cref="FlashAnythingService"/>, which performs the work and emits the resulting
/// <see cref="AppEvent"/>(s). Intents describe requests, not state changes; they have no
/// reducer (the service's dispatch interprets them).
/// </summary>
public abstract record AppIntent
{
    private protected AppIntent() { }

    /// <summary>Re-enumerate flashable targets now (in addition to live hotplug).</summary>
    public sealed record Refresh : AppIntent;

    /// <summary>Focus a target (null clears the selection).</summary>
    public sealed record SelectTarget(DeviceId? Id) : AppIntent;

    /// <summary>
    /// Load a firmware image from a path. <paramref name="BinBaseAddress"/> places a raw
    /// binary (.bin); it is ignored for formats that carry their own addresses (.hex).
    /// </summary>
    public sealed record LoadFirmware(string Path, uint BinBaseAddress = 0x08000000) : AppIntent;

    /// <summary>Flash the loaded firmware to one target.</summary>
    public sealed record Flash(DeviceId Id) : AppIntent;

    /// <summary>Flash the loaded firmware to every detected target.</summary>
    public sealed record FlashAll : AppIntent;

    /// <summary>
    /// Arm autoflash for a family/provider (matched against the provider name) with the given
    /// options. Requires a firmware image already loaded; flashes matching passively-identified
    /// targets as they are plugged in, until <see cref="DisarmAutoflash"/>.
    /// </summary>
    /// <param name="Family">The provider/family to arm for.</param>
    /// <param name="Options">Flash options applied to every automatic flash.</param>
    /// <param name="Ports">
    /// The serial ports to bind, for a probe-identified family. Each is resolved at arm time to the
    /// identity of the bridge currently behind it, and that identity is what is bound — a COM name
    /// is not an identity, and Windows recycles them (adr.md Decision 8). Empty for passive
    /// families, which identify themselves and need no binding.
    /// </param>
    /// <param name="Repeat">
    /// Whether a bound fixture may flash a succession of boards. Default
    /// <see cref="RepeatMode.None"/> — one flash per bridge per armed session.
    /// </param>
    public sealed record ArmAutoflash(
        string Family,
        FlashOptions Options,
        ImmutableArray<SerialPortName> Ports = default,
        RepeatMode Repeat = RepeatMode.None) : AppIntent;

    /// <summary>Disarm autoflash (stop automatic flashing).</summary>
    public sealed record DisarmAutoflash : AppIntent;
}
