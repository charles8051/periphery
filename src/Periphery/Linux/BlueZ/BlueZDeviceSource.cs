// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus;
using Periphery.Linux.DBus.Core;

namespace Periphery.Linux.BlueZ;

/// <summary>
/// The BlueZ leg of Linux enumeration (ADR-0091 D1–D4). It asks BlueZ for its objects over a
/// private system-bus connection and maps the bonded devices. Every failure yields no devices and is
/// logged once per instance. <see cref="LinuxDeviceProvider"/> holds one instance for the process
/// (D3).
/// </summary>
internal sealed class BlueZDeviceSource
{
    /// <summary>The deadline on each exchange: the connection, then each call (D2).</summary>
    internal static readonly TimeSpan ExchangeDeadline = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<DBusConnection>> _connect;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);
    private volatile bool _accessDenied;

    internal BlueZDeviceSource(Func<CancellationToken, Task<DBusConnection>> connect, TimeProvider timeProvider, ILogger logger)
    {
        _connect = connect;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The bonded BlueZ devices, enriched like every other Linux device. Empty when BlueZ cannot be
    /// reached or answers with an error.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal async Task<ImmutableArray<DeviceInfo>> EnumerateAsync(CancellationToken ct)
    {
        if (_accessDenied)
            return ImmutableArray<DeviceInfo>.Empty;

        bool connected = false;
        try
        {
            await using var connection = await WithDeadlineAsync(_connect, ct).ConfigureAwait(false);
            connected = true;

            var owner = await WithDeadlineAsync(t => connection.CallAsync(
                DBusMessage.Call(DBusConnection.BusName, DBusConnection.BusPath, DBusConnection.BusName, "GetNameOwner", BlueZInventory.Service), t), ct)
                .ConfigureAwait(false);
            if (owner.Type == DBusMessageType.Error)
                return Fail(owner.ErrorName!);
            if (owner.Body is not [DBusString { Value: var uniqueName }])
                throw new DBusProtocolException("GetNameOwner did not return a name.");

            // D2: to the unique name, never the well-known one, so no call can start bluetoothd.
            var reply = await WithDeadlineAsync(t => connection.CallAsync(
                DBusMessage.Call(uniqueName, "/", BlueZInventory.ObjectManagerInterface, "GetManagedObjects"), t), ct)
                .ConfigureAwait(false);
            if (reply.Type == DBusMessageType.Error)
                return Fail(reply.ErrorName!);
            if (reply.Signature != BlueZInventory.ManagedObjectsSignature)
                throw new DBusProtocolException($"GetManagedObjects returned '{reply.Signature}'.");

            return BlueZInventory.Map(reply.Body[0])
                .Select(device => EnrichmentPipeline.RunRegisteredSync(device, ct, _logger))
                .ToImmutableArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogOnce("Timeout", LogLevel.Warning,
                "BlueZ did not answer within {Deadline}. Bluetooth devices are left out.", ExchangeDeadline);
        }
        catch (SocketException ex) when (!connected)
        {
            LogOnce("NoSystemBus", LogLevel.Information,
                "No system bus to reach BlueZ through ({Error}). Bluetooth devices are left out.", ex.SocketErrorCode);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            LogOnce("Disconnected", LogLevel.Warning,
                "The system bus connection dropped while reading BlueZ ({Error}). Bluetooth devices are left out.", ex.Message);
        }
        catch (DBusAuthenticationException ex)
        {
            LogOnce("AuthenticationRefused", LogLevel.Warning, "{Error} Bluetooth devices are left out.", ex.Message);
        }
        catch (DBusProtocolException ex)
        {
            LogOnce("ProtocolError", LogLevel.Warning,
                "BlueZ or the bus sent a message Periphery cannot read ({Error}). Bluetooth devices are left out.", ex.Message);
        }
        return ImmutableArray<DeviceInfo>.Empty;
    }

    private ImmutableArray<DeviceInfo> Fail(string errorName)
    {
        switch (BlueZInventory.ClassifyError(errorName))
        {
            case BlueZFailureKind.Absent:
                LogOnce("Absent", LogLevel.Information,
                    "BlueZ is not running ({Error}). Bluetooth devices are left out.", errorName);
                break;
            case BlueZFailureKind.AccessDenied:
                _accessDenied = true;
                LogOnce(errorName, LogLevel.Warning,
                    "The system bus refused this process access to BlueZ ({Error}). Bluetooth devices are left out, and BlueZ is not asked again in this process.",
                    errorName);
                break;
            default:
                LogOnce(errorName, LogLevel.Warning,
                    "BlueZ answered with {Error}. Bluetooth devices are left out.", errorName);
                break;
        }
        return ImmutableArray<DeviceInfo>.Empty;
    }

    private async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> exchange, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(ExchangeDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        return await exchange(linked.Token).ConfigureAwait(false);
    }

#pragma warning disable CA2254 // One template per call site; the key, not the template, decides whether it logs.
    private void LogOnce(string key, LogLevel level, string template, params object?[] args)
    {
        if (_logged.TryAdd(key, 0))
            _logger.Log(level, template, args);
    }
#pragma warning restore CA2254
}
