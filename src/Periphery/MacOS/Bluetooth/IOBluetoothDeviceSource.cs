// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Periphery.MacOS.Bluetooth.Core;

namespace Periphery.MacOS.Bluetooth;

/// <summary>
/// The IOBluetooth leg of macOS enumeration (ADR-0093 D1–D3). It asks for the bonds only when this
/// process may use Bluetooth, and logs each reason for not asking once. <see cref="MacOSDeviceProvider"/>
/// holds one instance for the process.
/// </summary>
internal sealed class IOBluetoothDeviceSource
{
    private readonly Func<BluetoothAuthorization?> _authorization;
    private readonly Func<ImmutableArray<IOBluetoothBond>?> _bonds;
    private readonly Func<ImmutableArray<HidLink>> _links;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    // D3 amendment: the transports each address's HID node has shown, kept for the process.
    private ImmutableDictionary<BluetoothAddress, BluetoothTransports> _known =
        ImmutableDictionary<BluetoothAddress, BluetoothTransports>.Empty;

    internal IOBluetoothDeviceSource(
        Func<BluetoothAuthorization?> authorization,
        Func<ImmutableArray<IOBluetoothBond>?> bonds,
        Func<ImmutableArray<HidLink>> links,
        ILogger logger)
    {
        _authorization = authorization;
        _bonds = bonds;
        _links = links;
        _logger = logger;
    }

    /// <summary>
    /// The bonds, enriched like every other macOS device. <see langword="null"/> when IOBluetooth
    /// cannot be asked, which the watch treats as no news rather than as every bond leaving (D4).
    /// </summary>
    internal ImmutableArray<DeviceInfo>? Snapshot(CancellationToken ct = default)
    {
        if (_authorization() is not { } authorization)
        {
            LogOnce("unavailable", "IOBluetooth could not be loaded, so bonded Bluetooth devices are not reported.");
            return null;
        }

        if (IOBluetoothInventory.Unavailable(authorization) is { } reason)
        {
            LogOnce(authorization.ToString(), reason);
            return null;
        }

        if (_bonds() is not { } bonds)
        {
            LogOnce("unavailable", "IOBluetooth could not be loaded, so bonded Bluetooth devices are not reported.");
            return null;
        }

        ImmutableInterlocked.Update(ref _known, (current, links) => IOBluetoothInventory.Learn(current, links), _links());
        var known = Volatile.Read(ref _known);
        return [.. IOBluetoothInventory.Map(bonds, known).Select(device => EnrichmentPipeline.RunRegisteredSync(device, ct, _logger))];
    }

    /// <summary>The bonds, or none when IOBluetooth cannot be asked.</summary>
    internal ImmutableArray<DeviceInfo> Enumerate(CancellationToken ct) => Snapshot(ct) ?? [];

    private void LogOnce(string key, string message)
    {
        if (_logged.TryAdd(key, 0))
            _logger.LogWarning("{Message}", message);
    }
}
