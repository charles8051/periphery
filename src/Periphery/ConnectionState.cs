// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery;

/// <summary>
/// Observable session-openability state of a
/// <see cref="DeviceProxyBase{TDevice,TException}"/>. Distinct from device
/// <em>presence</em> (which the <see cref="DeviceTracker"/> owns): a device can be
/// enumerated and present yet unopenable, in which case the proxy reaches
/// <see cref="GaveUp"/>.
/// </summary>
public enum ConnectionState
{
    /// <summary>No open session and not currently attempting one (idle / closed).</summary>
    Disconnected,

    /// <summary>A reconnect attempt is scheduled or in flight.</summary>
    Connecting,

    /// <summary>The device is open and ready for I/O.</summary>
    Open,

    /// <summary>
    /// A device reset is in flight (ADR-0060): the proxy issued a
    /// <see cref="RecoveryDirective.Reset"/> and is driving the re-open.
    /// Transient like <see cref="Connecting"/>, but distinct so a health probe
    /// or UI can tell "cycling the hardware" from "retrying the open".
    /// </summary>
    Resetting,

    /// <summary>
    /// The injected <see cref="IRecoveryPolicy"/> stopped retrying. The proxy
    /// stays here until the device re-enumerates, which resets the attempt budget.
    /// This is the "enumerated but unopenable" signal a health probe maps to
    /// Degraded / Unhealthy.
    /// </summary>
    GaveUp,
}
