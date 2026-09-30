// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus;
using Periphery.Linux.DBus.Core;

namespace Periphery.Linux.BlueZ;

/// <summary>
/// The shell around <see cref="BlueZWatch"/> (ADR-0091 D8). It owns one private connection, makes
/// the subscription calls, feeds every message and wake time to the pure core, and carries out what
/// the core asks: send a snapshot request, log a problem, raise an edge. It runs its own receive
/// loop on the connection's stream.
/// </summary>
internal sealed class BlueZMonitor : IAsyncDisposable
{
    /// <summary>The three match rules, added once per connection (D8).</summary>
    internal static readonly string[] MatchRules =
    [
        "type='signal',sender='org.bluez',interface='org.freedesktop.DBus.ObjectManager'",
        "type='signal',sender='org.bluez',interface='org.freedesktop.DBus.Properties',member='PropertiesChanged',arg0='org.bluez.Device1'",
        "type='signal',sender='org.freedesktop.DBus',interface='org.freedesktop.DBus',member='NameOwnerChanged',arg0='org.bluez'",
    ];

    // Once per process, as D3 asks, across every monitor a process starts.
    private static readonly ConcurrentDictionary<string, byte> SharedLogged = new(StringComparer.Ordinal);

    private readonly Func<CancellationToken, Task<DBusConnection>> _connect;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<BlueZEdge> _raise;
    private readonly ConcurrentDictionary<string, byte> _logged;
    private readonly CancellationTokenSource _stop = new();

    private DBusConnection? _connection;
    private BlueZWatchState _state = BlueZWatchState.Initial;
    private Task<DBusMessage>? _receive;
    private Task? _loop;

    internal BlueZMonitor(
        Func<CancellationToken, Task<DBusConnection>> connect,
        TimeProvider timeProvider,
        ILogger logger,
        Action<BlueZEdge> raise,
        ConcurrentDictionary<string, byte>? logged = null)
    {
        _connect = connect;
        _timeProvider = timeProvider;
        _logger = logger;
        _raise = raise;
        _logged = logged ?? SharedLogged;
    }

    /// <summary>The state after the last step. For tests.</summary>
    internal BlueZWatchState State => _state;

    /// <summary>
    /// Subscribes, then seeds silently from the first snapshot before returning, so the watcher's
    /// own enumeration and this monitor start from the same devices. Every exchange has a deadline.
    /// If BlueZ cannot be reached, it logs once and watches nothing; it never throws for that.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal async Task StartAsync(CancellationToken ct)
    {
        bool connected = false;
        try
        {
            _connection = await WithDeadlineAsync(_connect, ct).ConfigureAwait(false);
            connected = true;

            foreach (var rule in MatchRules)
            {
                var added = await WithDeadlineAsync(t => _connection.CallAsync(
                    DBusMessage.Call(DBusConnection.BusName, DBusConnection.BusPath, DBusConnection.BusName, "AddMatch", rule), t), ct)
                    .ConfigureAwait(false);
                if (added.Type == DBusMessageType.Error)
                {
                    Log(added.ErrorName!, LogLevel.Warning,
                        $"The system bus refused a BlueZ subscription ({added.ErrorName}). Bluetooth devices are not watched.");
                    await CloseAsync().ConfigureAwait(false);
                    return;
                }
            }

            var owner = await WithDeadlineAsync(t => _connection.CallAsync(
                DBusMessage.Call(DBusConnection.BusName, DBusConnection.BusPath, DBusConnection.BusName, "GetNameOwner", BlueZInventory.Service), t), ct)
                .ConfigureAwait(false);
            string? ownerName = owner.Body is [DBusString { Value: var name }] && owner.Type == DBusMessageType.MethodReturn ? name : null;

            await ApplyAsync(new WatchStarted(ownerName), ct).ConfigureAwait(false);
            while (!_state.Seeded)
                await PumpAsync(ct).ConfigureAwait(false);

            _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                       or DBusProtocolException or DBusAuthenticationException)
        {
            Log(connected ? ex.GetType().Name : "NoSystemBus", connected ? LogLevel.Warning : LogLevel.Information,
                $"BlueZ could not be watched ({ex.Message}). Bluetooth devices are not watched.");
            await CloseAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested && !_state.Stopped)
                await PumpAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or DBusProtocolException)
        {
            await ApplyAsync(new ConnectionLost(ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The BlueZ watch stopped unexpectedly.");
        }
    }

    // Waits for the next message or the core's next wake time, whichever comes first, and steps. A
    // read in flight is never cancelled by a wake: it carries over to the next pump.
    private async Task PumpAsync(CancellationToken ct)
    {
        _receive ??= _connection!.ReceiveAsync(_stop.Token);

        if (BlueZWatch.NextWake(_state) is not { } wakeAt)
        {
            var message = await _receive.WaitAsync(ct).ConfigureAwait(false);
            _receive = null;
            await ApplyAsync(new MessageReceived(message), ct).ConfigureAwait(false);
            return;
        }

        var delay = wakeAt - _timeProvider.GetUtcNow();
        using var cancelWake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wake = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _timeProvider, cancelWake.Token);
        var first = await Task.WhenAny(_receive, wake).ConfigureAwait(false);
        await cancelWake.CancelAsync().ConfigureAwait(false);

        if (first == _receive)
        {
            var message = await _receive.ConfigureAwait(false);
            _receive = null;
            await ApplyAsync(new MessageReceived(message), ct).ConfigureAwait(false);
        }
        else
        {
            await wake.ConfigureAwait(false);
            await ApplyAsync(new WakeTime(), ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(BlueZObservation observation, CancellationToken ct)
    {
        var step = BlueZWatch.Step(_state, observation, _timeProvider.GetUtcNow());
        _state = step.State;

        foreach (var edge in step.Edges)
            Raise(edge);

        foreach (var effect in step.Effects)
        {
            switch (effect)
            {
                case RequestSnapshot request:
                    uint serial = await _connection!.SendAsync(
                        DBusMessage.Call(request.Destination, "/", BlueZInventory.ObjectManagerInterface, "GetManagedObjects"), ct)
                        .ConfigureAwait(false);
                    _state = BlueZWatch.Step(_state, new SnapshotSent(serial), _timeProvider.GetUtcNow()).State;
                    break;
                case ReportProblem problem:
                    Log(problem.Key, problem.Level, problem.Message);
                    break;
            }
        }
    }

    private void Raise(BlueZEdge edge)
    {
        var device = EnrichmentPipeline.RunRegisteredSync(edge.Device, CancellationToken.None, _logger);
        try
        {
            _raise(edge with { Device = device });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A handler threw on BlueZ edge {Edge} for {DeviceId}.", edge.Kind, edge.Device.Id);
        }
    }

    private async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> exchange, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(BlueZWatch.SnapshotDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        return await exchange(linked.Token).ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);

        // The read in flight ends when the stream closes; observe it so its exception is not lost.
        if (_receive is { } receive)
        {
            try { await receive.ConfigureAwait(false); }
            catch (Exception) { }
            _receive = null;
        }
    }

    // The message goes in as an argument: it can hold braces, such as a signature.
    private void Log(string key, LogLevel level, string message)
    {
        if (_logged.TryAdd(key, 0))
            _logger.Log(level, "{Problem}", message);
    }
}
