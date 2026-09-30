// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Periphery.MacOS.Bluetooth.Core;

namespace Periphery.MacOS.Bluetooth;

/// <summary>
/// The watch shell for bonds (ADR-0093 D4). It polls a snapshot on a clock it is given, steps
/// <see cref="IOBluetoothInventory.Step"/> and raises the edges.
/// </summary>
internal sealed class IOBluetoothMonitor : IAsyncDisposable
{
    /// <summary>D4: how often the bonds are read. A read costs about 0.1 ms after the first.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<ImmutableArray<DeviceInfo>?> _snapshot;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<IOBluetoothEdge> _raise;
    private readonly CancellationTokenSource _stop = new();
    private ImmutableDictionary<DeviceId, DeviceInfo> _held = ImmutableDictionary<DeviceId, DeviceInfo>.Empty;
    private Task? _loop;

    internal IOBluetoothMonitor(
        Func<ImmutableArray<DeviceInfo>?> snapshot, TimeProvider timeProvider, ILogger logger, Action<IOBluetoothEdge> raise)
    {
        _snapshot = snapshot;
        _timeProvider = timeProvider;
        _logger = logger;
        _raise = raise;
    }

    /// <summary>The bonds held after the last poll.</summary>
    internal ImmutableDictionary<DeviceId, DeviceInfo> Held => Volatile.Read(ref _held);

    /// <summary>Raised once each poll has stepped and raised its edges, whatever happened.</summary>
    internal event Action? Polled;

    /// <summary>
    /// Seeds from one snapshot without raising anything, since the watcher's own enumeration
    /// announces those bonds, then starts polling.
    /// </summary>
    internal void Start()
    {
        Volatile.Write(ref _held, IOBluetoothInventory.Step(_held, _snapshot()).Held);
        _loop = PollAsync(_stop.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Task.Delay(PollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var (held, edges) = IOBluetoothInventory.Step(_held, _snapshot());
                Volatile.Write(ref _held, held);
                foreach (var edge in edges)
                {
                    try
                    {
                        _raise(edge);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "A handler threw on a Bluetooth {Edge} for {DeviceId}", edge.Kind, edge.Device.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading the Bluetooth bonds failed; the next poll retries");
            }
            finally
            {
                Polled?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
