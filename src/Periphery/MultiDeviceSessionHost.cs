// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Manages a dynamic set of <see cref="DeviceSessionHost{TSession}"/>
/// instances — one per device matching a group filter. Automatically
/// creates a session host when a new device appears and tears it down
/// on disposal.
///
/// <para>Built on top of <see cref="MultiDeviceTracker"/> and composes
/// naturally with the existing session host stack.</para>
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
public sealed class MultiDeviceSessionHost<TSession> : IAsyncDisposable
    where TSession : class
{
    private readonly MultiDeviceTracker _multiTracker;
    private readonly DeviceWatcher? _ownedWatcher;
    private readonly Func<DeviceInfo, CancellationToken, Task<TSession>> _createSession;
    private readonly Func<TSession, Task>? _onSessionEnded;
    private readonly Func<TSession, CancellationToken, Task>? _whileSessionActive;
    private readonly IRecoveryPolicy? _recoveryPolicy;
    private readonly IDeviceReset? _deviceReset;
    private readonly IResetSafetyGate? _resetSafetyGate;
    private readonly bool _faultedNodeRecovery;
    private readonly ConcurrentDictionary<DeviceId, DeviceSessionHost<TSession>> _hosts = new();
    private bool _disposed;

    private MultiDeviceSessionHost(
        MultiDeviceTracker multiTracker,
        DeviceWatcher? ownedWatcher,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded,
        Func<TSession, CancellationToken, Task>? whileSessionActive,
        IRecoveryPolicy? recoveryPolicy,
        IDeviceReset? deviceReset,
        IResetSafetyGate? resetSafetyGate,
        bool faultedNodeRecovery)
    {
        _multiTracker = multiTracker;
        _ownedWatcher = ownedWatcher;
        _createSession = createSession;
        _onSessionEnded = onSessionEnded;
        _whileSessionActive = whileSessionActive;
        _recoveryPolicy = recoveryPolicy;
        _deviceReset = deviceReset;
        _resetSafetyGate = resetSafetyGate;
        _faultedNodeRecovery = faultedNodeRecovery;
        _multiTracker.DeviceAdded += OnDeviceAdded;
    }

    /// <summary>
    /// Creates a self-contained group session host that owns its own
    /// <see cref="DeviceWatcher"/>.
    /// </summary>
    /// <param name="configure">Configures the group filter criteria.</param>
    /// <param name="createSession">
    /// Factory delegate that creates a session from a <see cref="DeviceInfo"/>
    /// snapshot. Called each time a matching device becomes active.
    /// </param>
    /// <param name="onSessionEnded">
    /// Optional teardown delegate invoked when a session ends (device
    /// disconnected or host disposed).
    /// </param>
    /// <param name="whileSessionActive">
    /// Optional supervised background worker for each active session.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Device recovery policy (retry / reset / give-up) forwarded to the underlying device
    /// handle of every per-device <see cref="DeviceSessionHost{TSession}"/> in the group;
    /// defaults to <see cref="ExponentialBackoffRecoveryPolicy.Default"/>
    /// (retry forever, no reset) when <see langword="null"/>.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060) fanned out to every per-device handle.
    /// <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset, shared across the group.
    /// <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11) fanned out to every
    /// per-device handle. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    /// <param name="name">Optional human-readable label for the group.</param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    /// <returns>A started group session host. Dispose when done.</returns>
    public static async Task<MultiDeviceSessionHost<TSession>> StartAsync(
        Action<DeviceFilter> configure,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        Func<TSession, CancellationToken, Task>? whileSessionActive = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false,
        string? name = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(createSession);

        var multiTracker = new MultiDeviceTracker(configure, name);
        var watcher = Devices.Watch().AddMultiTracker(multiTracker);
        var host = new MultiDeviceSessionHost<TSession>(
            multiTracker, watcher, createSession, onSessionEnded, whileSessionActive,
            recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery);

        try
        {
            await watcher.StartAsync(ct).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a group session host that borrows an existing
    /// <see cref="MultiDeviceTracker"/> already attached to a caller-owned
    /// <see cref="DeviceWatcher"/>.
    /// </summary>
    /// <param name="multiTracker">
    /// An existing group tracker, already attached to a running
    /// <see cref="DeviceWatcher"/>.
    /// </param>
    /// <param name="createSession">
    /// Factory delegate that creates a session from a <see cref="DeviceInfo"/>
    /// snapshot.
    /// </param>
    /// <param name="onSessionEnded">
    /// Optional teardown delegate.
    /// </param>
    /// <param name="whileSessionActive">
    /// Optional supervised background worker for each active session.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Device recovery policy (retry / reset / give-up) forwarded to the underlying device
    /// handle of every per-device <see cref="DeviceSessionHost{TSession}"/> in the group;
    /// defaults to <see cref="ExponentialBackoffRecoveryPolicy.Default"/>
    /// (retry forever, no reset) when <see langword="null"/>.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060) fanned out to every per-device handle.
    /// <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset, shared across the group.
    /// <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11) fanned out to every
    /// per-device handle. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    /// <returns>A group session host. Dispose when done.</returns>
    public static MultiDeviceSessionHost<TSession> Create(
        MultiDeviceTracker multiTracker,
        Func<DeviceInfo, CancellationToken, Task<TSession>> createSession,
        Func<TSession, Task>? onSessionEnded = null,
        Func<TSession, CancellationToken, Task>? whileSessionActive = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(multiTracker);
        ArgumentNullException.ThrowIfNull(createSession);

        var host = new MultiDeviceSessionHost<TSession>(
            multiTracker, ownedWatcher: null, createSession, onSessionEnded, whileSessionActive,
            recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery);

        // Create session hosts for any existing child trackers
        foreach (var (deviceId, tracker) in multiTracker.Trackers)
            host.CreateHostForTracker(deviceId, tracker);

        return host;
    }

    // ── Public state ───────────────────────────────────────────────────

    /// <summary>
    /// All per-device session hosts, keyed by <see cref="DeviceInfo.Id"/>.
    /// </summary>
    public IReadOnlyDictionary<DeviceId, DeviceSessionHost<TSession>> Hosts => _hosts;

    /// <summary>
    /// The underlying group tracker.
    /// </summary>
    public MultiDeviceTracker MultiTracker => _multiTracker;

    /// <summary>
    /// Number of per-device session hosts.
    /// </summary>
    public int Count => _hosts.Count;

    // ── Events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a new per-device <see cref="DeviceSessionHost{TSession}"/>
    /// is created for a newly-seen device.
    /// </summary>
    public event EventHandler<DeviceSessionHost<TSession>>? SessionHostAdded;

    // ── Disposal ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _multiTracker.DeviceAdded -= OnDeviceAdded;

        foreach (var (_, host) in _hosts)
            await host.DisposeAsync().ConfigureAwait(false);
        _hosts.Clear();

        if (_ownedWatcher is not null)
            await _ownedWatcher.DisposeAsync().ConfigureAwait(false);
    }

    // ── Private ────────────────────────────────────────────────────────

    private void OnDeviceAdded(object? sender, DeviceTracker tracker)
    {
        if (_disposed) return;
        // Find the device ID from the group tracker's dictionary
        foreach (var (deviceId, t) in _multiTracker.Trackers)
        {
            if (ReferenceEquals(t, tracker))
            {
                CreateHostForTracker(deviceId, tracker);
                return;
            }
        }
    }

    private void CreateHostForTracker(string deviceId, DeviceTracker tracker)
    {

        var sessionHost = DeviceSessionHost<TSession>.Create(
            tracker,
            createSession: _createSession,
            onSessionEnded: _onSessionEnded,
            whileSessionActive: _whileSessionActive,
            recoveryPolicy: _recoveryPolicy,
            deviceReset: _deviceReset,
            resetSafetyGate: _resetSafetyGate,
            faultedNodeRecovery: _faultedNodeRecovery);

        if (_hosts.TryAdd(deviceId, sessionHost))
            SessionHostAdded?.Invoke(this, sessionHost);
    }
}
