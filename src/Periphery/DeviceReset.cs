// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Access to the platform <see cref="IDeviceReset"/> mechanism.
/// <see cref="DeviceProxyBase{TDevice,TException}"/> uses
/// <see cref="PlatformDefault"/> when no custom reset is injected, so reset is
/// available wherever the OS supports it and is simply gated by the
/// <see cref="IRecoveryPolicy"/> — which only returns
/// <see cref="RecoveryDirective.Reset"/> when it chooses to, so the default
/// (retry-only) policy never resets and prior behavior is preserved.
/// </summary>
public static class DeviceReset
{
    /// <summary>
    /// The reset mechanism for the current OS: a cfgmgr32-backed implementation
    /// on Windows, and a no-op (<see cref="NullDeviceReset"/>) elsewhere.
    /// </summary>
    public static IDeviceReset PlatformDefault { get; } =
        OperatingSystem.IsWindows()
            ? new Windows.WindowsDeviceReset()
            : NullDeviceReset.Instance;
}

/// <summary>
/// An <see cref="IDeviceReset"/> that advertises no strategies and resets
/// nothing — the non-Windows default, and an explicit "this device is not
/// resettable" injection.
/// </summary>
public sealed class NullDeviceReset : IDeviceReset
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullDeviceReset Instance = new();

    private NullDeviceReset() { }

    /// <inheritdoc/>
    public IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device) => [];

    /// <inheritdoc/>
    public ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct)
        => new(ResetOutcome.NotSupported);
}
