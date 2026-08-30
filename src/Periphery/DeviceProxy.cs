// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Sealed, delegate-configured device handle for application code.
/// Provides the same reconnect-resilient lifecycle as
/// <see cref="DeviceProxyBase{TDevice,TException}"/> without requiring
/// a derived class — configure behaviour via <see cref="Func{T,TResult}"/>
/// delegates passed to <see cref="OpenAsync"/>.
/// </summary>
/// <typeparam name="TDevice">
/// The platform device type. Must be a reference type implementing
/// <see cref="IAsyncDisposable"/>.
/// </typeparam>
/// <example>
/// <code>
/// await using var handle = await DeviceProxy&lt;AsyncSerialPort&gt;.OpenAsync(
///     profile: scannerProfile,
///     openDevice: (info, ct) => Task.FromResult(
///         new AsyncSerialPort(info.PortName!.Value.Value, 115200)),
///     onActivated: async (port, ct) =>
///     {
///         port.Port.Write("HB\r\n");
///         await Task.Delay(200, ct);
///     });
/// </code>
/// </example>
public sealed class DeviceProxy<TDevice>
    : DeviceProxyBase<TDevice, Exception>
    where TDevice : class, IAsyncDisposable
{
    private readonly Func<DeviceInfo, CancellationToken, Task<TDevice>> _openDevice;
    private readonly Func<TDevice, CancellationToken, Task>? _onActivated;
    private readonly Func<TDevice, Task>? _onDeactivated;
    private readonly Func<TDevice, CancellationToken, Task>? _whileOpen;

    private DeviceProxy(
        DeviceTracker tracker,
        DeviceWatcher watcher,
        Func<DeviceInfo, CancellationToken, Task<TDevice>> openDevice,
        Func<TDevice, CancellationToken, Task>? onActivated,
        Func<TDevice, Task>? onDeactivated,
        Func<TDevice, CancellationToken, Task>? whileOpen,
        IRecoveryPolicy? recoveryPolicy,
        IDeviceReset? deviceReset,
        IResetSafetyGate? resetSafetyGate,
        bool faultedNodeRecovery)
        : base(tracker, watcher, recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery)
    {
        _openDevice = openDevice;
        _onActivated = onActivated;
        _onDeactivated = onDeactivated;
        _whileOpen = whileOpen;
    }

    private DeviceProxy(
        DeviceTracker tracker,
        Func<DeviceInfo, CancellationToken, Task<TDevice>> openDevice,
        Func<TDevice, CancellationToken, Task>? onActivated,
        Func<TDevice, Task>? onDeactivated,
        Func<TDevice, CancellationToken, Task>? whileOpen,
        IRecoveryPolicy? recoveryPolicy,
        IDeviceReset? deviceReset,
        IResetSafetyGate? resetSafetyGate,
        bool faultedNodeRecovery)
        : base(tracker, recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery)
    {
        _openDevice = openDevice;
        _onActivated = onActivated;
        _onDeactivated = onDeactivated;
        _whileOpen = whileOpen;
    }

    /// <inheritdoc/>
    protected override Task<TDevice> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct)
        => _openDevice(deviceInfo, ct);

    /// <inheritdoc/>
    protected override Task OnActivatedAsync(
        TDevice device, CancellationToken ct)
        => _onActivated?.Invoke(device, ct) ?? Task.CompletedTask;

    /// <inheritdoc/>
    protected override Task OnDeactivatedAsync(TDevice device)
        => _onDeactivated?.Invoke(device) ?? Task.CompletedTask;

    /// <inheritdoc/>
    protected override bool HasWorker => _whileOpen is not null;

    /// <inheritdoc/>
    protected override Task WhileOpenAsync(TDevice device, CancellationToken ct)
        => _whileOpen?.Invoke(device, ct) ?? Task.CompletedTask;

    /// <summary>
    /// Creates a delegate-configured device handle and starts the watcher.
    /// </summary>
    /// <param name="profile">
    /// The device profile describing which device to track. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <param name="openDevice">
    /// Factory delegate that opens the platform device from a
    /// <see cref="DeviceInfo"/> snapshot. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="onActivated">
    /// Optional init-gate delegate invoked inside the open lock BEFORE
    /// <see cref="DeviceProxyBase{TDevice,TException}.IsOpen"/>
    /// becomes <see langword="true"/>. Throw to abort the connection.
    /// </param>
    /// <param name="onDeactivated">
    /// Optional teardown delegate invoked inside the open lock during close.
    /// </param>
    /// <param name="whileOpen">
    /// Optional supervised worker run while the device is open.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional recovery policy (retry / reset / give-up). Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when omitted.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060). <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in (ADR-0060 Decision 11). When <see langword="true"/>, an enumerated-but-
    /// faulted device that never reaches Active drives the reset ladder after a settle
    /// window. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    /// <returns>A started <see cref="DeviceProxy{TDevice}"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="profile"/> or <paramref name="openDevice"/>
    /// is <see langword="null"/>.
    /// </exception>
    public static async Task<DeviceProxy<TDevice>> OpenAsync(
        DeviceProfile profile,
        Func<DeviceInfo, CancellationToken, Task<TDevice>> openDevice,
        Func<TDevice, CancellationToken, Task>? onActivated = null,
        Func<TDevice, Task>? onDeactivated = null,
        Func<TDevice, CancellationToken, Task>? whileOpen = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(openDevice);

        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new DeviceProxy<TDevice>(
            tracker, watcher, openDevice, onActivated, onDeactivated, whileOpen,
            recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery);

        try
        {
            await watcher.StartAsync(ct).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a delegate-configured device handle that borrows an existing
    /// <see cref="DeviceTracker"/>. The caller is responsible for managing
    /// the <see cref="DeviceWatcher"/> lifetime. Use this when a single
    /// watcher powers multiple devices to reduce system calls.
    /// </summary>
    /// <param name="tracker">
    /// An existing tracker, already attached to a running
    /// <see cref="DeviceWatcher"/>. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="openDevice">
    /// Factory delegate that opens the platform device from a
    /// <see cref="DeviceInfo"/> snapshot. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="onActivated">
    /// Optional init-gate delegate invoked inside the open lock BEFORE
    /// <see cref="DeviceProxyBase{TDevice,TException}.IsOpen"/>
    /// becomes <see langword="true"/>. Throw to abort the connection.
    /// </param>
    /// <param name="onDeactivated">
    /// Optional teardown delegate invoked inside the open lock during close.
    /// </param>
    /// <param name="whileOpen">
    /// Optional supervised worker run while the device is open.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional recovery policy (retry / reset / give-up). Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when omitted.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060). <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11). <see langword="false"/>
    /// (the default) preserves prior behavior.
    /// </param>
    /// <returns>A <see cref="DeviceProxy{TDevice}"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tracker"/> or <paramref name="openDevice"/>
    /// is <see langword="null"/>.
    /// </exception>
    public static DeviceProxy<TDevice> Create(
        DeviceTracker tracker,
        Func<DeviceInfo, CancellationToken, Task<TDevice>> openDevice,
        Func<TDevice, CancellationToken, Task>? onActivated = null,
        Func<TDevice, Task>? onDeactivated = null,
        Func<TDevice, CancellationToken, Task>? whileOpen = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(openDevice);

        var handle = new DeviceProxy<TDevice>(
            tracker, openDevice, onActivated, onDeactivated, whileOpen,
            recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery);
        handle.CheckInitialState();
        return handle;
    }
}

/// <summary>
/// Non-generic, delegate-configured device handle for application code.
/// Provides reconnect-resilient lifecycle management without requiring a
/// device type — manage your own resources in closure-captured state.
/// </summary>
/// <remarks>
/// <para>This is the primary entry point for application code. Unlike
/// <see cref="DeviceProxy{TDevice}"/>, no <c>TDevice</c> type or
/// <see cref="IAsyncDisposable"/> wrapper is needed — just pass lambdas.</para>
/// <para>Two factory methods are available:</para>
/// <list type="bullet">
/// <item><see cref="OpenAsync"/> — self-contained: creates and owns its own
/// <see cref="DeviceTracker"/> and <see cref="DeviceWatcher"/>.</item>
/// <item><see cref="Create"/> — shared: borrows an existing
/// <see cref="DeviceTracker"/> already attached to a caller-owned
/// <see cref="DeviceWatcher"/>. Use this when a single watcher powers
/// multiple devices to reduce system calls.</item>
/// </list>
/// <para><b>Threading:</b> All events fire on thread-pool threads.
/// UI dispatch is the consumer's responsibility.</para>
/// </remarks>
/// <example>
/// <code>
/// await using var scanner = await DeviceProxy.OpenAsync(
///     profile: scannerProfile,
///     onActivated: async (deviceInfo, ct) =>
///     {
///         _port = new SerialPort(deviceInfo.PortName!.Value.Value, 115200);
///         _port.Open();
///     },
///     onDeactivated: _ =>
///     {
///         _port?.Close();
///         _port?.Dispose();
///         _port = null;
///         return Task.CompletedTask;
///     },
/// </code>
/// </example>
public sealed class DeviceProxy
    : DeviceProxyBase<DeviceProxy.Sentinel, Exception>
{
    private readonly Func<DeviceInfo, CancellationToken, Task>? _onActivated;
    private readonly Func<DeviceInfo, Task>? _onDeactivated;
    private readonly Func<DeviceInfo, CancellationToken, Task>? _whileOpen;

    /// <summary>
    /// Inert <see cref="IAsyncDisposable"/> stand-in for the closure model,
    /// which has no platform device handle. The base class is generic over
    /// <c>TDevice</c>; this sentinel satisfies that constraint and carries the
    /// <see cref="DeviceInfo"/> snapshot through to the closure hooks. It owns
    /// no resources, so <see cref="DisposeAsync"/> is a no-op.
    /// </summary>
    /// <remarks>
    /// Public only because the base class's generic argument leaks the type
    /// name into the type signature; it is never meant to be constructed or
    /// referenced by application code.
    /// </remarks>
    public sealed class Sentinel : IAsyncDisposable
    {
        internal Sentinel(DeviceInfo deviceInfo) => Info = deviceInfo;

        internal DeviceInfo Info { get; }

        /// <inheritdoc/>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private DeviceProxy(
        DeviceTracker tracker,
        DeviceWatcher ownedWatcher,
        Func<DeviceInfo, CancellationToken, Task>? onActivated,
        Func<DeviceInfo, Task>? onDeactivated,
        Func<DeviceInfo, CancellationToken, Task>? whileOpen,
        IRecoveryPolicy? recoveryPolicy,
        IDeviceReset? deviceReset,
        IResetSafetyGate? resetSafetyGate,
        bool faultedNodeRecovery)
        : base(tracker, ownedWatcher, recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery)
    {
        _onActivated = onActivated;
        _onDeactivated = onDeactivated;
        _whileOpen = whileOpen;
    }

    private DeviceProxy(
        DeviceTracker tracker,
        Func<DeviceInfo, CancellationToken, Task>? onActivated,
        Func<DeviceInfo, Task>? onDeactivated,
        Func<DeviceInfo, CancellationToken, Task>? whileOpen,
        IRecoveryPolicy? recoveryPolicy,
        IDeviceReset? deviceReset,
        IResetSafetyGate? resetSafetyGate,
        bool faultedNodeRecovery)
        : base(tracker, recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery)
    {
        _onActivated = onActivated;
        _onDeactivated = onDeactivated;
        _whileOpen = whileOpen;
    }

    // -------------------------------------------------------------------
    // Hooks — adapt the base's TDevice-shaped hooks to the closure model
    // -------------------------------------------------------------------

    /// <summary>
    /// "Opens" the sentinel device and runs the <c>onActivated</c> init gate.
    /// Running <c>onActivated</c> here (rather than in an
    /// <c>OnActivatedAsync</c> override, which the non-generic proxy does not
    /// have — it owns its own state machine) means an init-gate throw surfaces as a
    /// typed open-failure, so the inherited
    /// <see cref="DeviceProxyBase{TDevice,TException}.OpenFailed"/> event fires
    /// for it — preserving the non-generic proxy's original contract.
    /// </summary>
    /// <inheritdoc/>
    protected override async Task<Sentinel> OpenDeviceAsync(
        DeviceInfo deviceInfo, CancellationToken ct)
    {
        if (_onActivated is not null)
            await _onActivated(deviceInfo, ct).ConfigureAwait(false);

        return new Sentinel(deviceInfo);
    }

    /// <inheritdoc/>
    protected override Task OnDeactivatedAsync(Sentinel device)
        => _onDeactivated?.Invoke(device.Info) ?? Task.CompletedTask;

    /// <inheritdoc/>
    protected override bool HasWorker => _whileOpen is not null;

    /// <inheritdoc/>
    protected override Task WhileOpenAsync(Sentinel device, CancellationToken ct)
        => _whileOpen?.Invoke(device.Info, ct) ?? Task.CompletedTask;

    // -------------------------------------------------------------------
    // Factories
    // -------------------------------------------------------------------

    /// <summary>
    /// Creates a self-contained device handle that owns its own
    /// <see cref="DeviceTracker"/> and <see cref="DeviceWatcher"/>.
    /// </summary>
    /// <param name="profile">
    /// The device profile describing which device to track. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <param name="onActivated">
    /// Optional delegate invoked when the device becomes active. Throw to
    /// abort the connection attempt. Receives the <see cref="DeviceInfo"/>
    /// snapshot and a per-connection <see cref="CancellationToken"/>.
    /// </param>
    /// <param name="onDeactivated">
    /// Optional teardown delegate invoked when the device disconnects.
    /// Receives the last-known <see cref="DeviceInfo"/> snapshot.
    /// </param>
    /// <param name="whileOpen">
    /// Optional supervised worker run while the device is open.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional recovery policy (retry / reset / give-up). Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when omitted.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060). <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in (ADR-0060 Decision 11). When <see langword="true"/>, an enumerated-but-
    /// faulted device that never reaches Active drives the reset ladder after a settle
    /// window. <see langword="false"/> (the default) preserves prior behavior.
    /// </param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    /// <returns>A started <see cref="DeviceProxy"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="profile"/> is <see langword="null"/>.
    /// </exception>
    public static async Task<DeviceProxy> OpenAsync(
        DeviceProfile profile,
        Func<DeviceInfo, CancellationToken, Task>? onActivated = null,
        Func<DeviceInfo, Task>? onDeactivated = null,
        Func<DeviceInfo, CancellationToken, Task>? whileOpen = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var tracker = new DeviceTracker(profile.Filter, profile.Name);
        var watcher = Devices.Watch().AddTracker(tracker);
        var handle = new DeviceProxy(
            tracker, watcher, onActivated, onDeactivated, whileOpen,
            recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery);

        try
        {
            await watcher.StartAsync(ct).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a device handle that borrows an existing
    /// <see cref="DeviceTracker"/>. The caller is responsible for managing
    /// the <see cref="DeviceWatcher"/> lifetime. Use this when a single
    /// watcher powers multiple devices to reduce system calls.
    /// </summary>
    /// <param name="tracker">
    /// An existing tracker, already attached to a running
    /// <see cref="DeviceWatcher"/>. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="onActivated">
    /// Optional delegate invoked when the device becomes active.
    /// </param>
    /// <param name="onDeactivated">
    /// Optional teardown delegate invoked when the device disconnects.
    /// </param>
    /// <param name="whileOpen">
    /// Optional supervised worker run while the device is open.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional recovery policy (retry / reset / give-up). Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/> when omitted.
    /// </param>
    /// <param name="deviceReset">
    /// Optional reset capability (ADR-0060). <see langword="null"/> ⇒ no resets.
    /// </param>
    /// <param name="resetSafetyGate">
    /// Optional gate consulted before each reset. <see langword="null"/> ⇒ always safe.
    /// </param>
    /// <param name="faultedNodeRecovery">
    /// Opt-in faulted-node recovery (ADR-0060 Decision 11). <see langword="false"/>
    /// (the default) preserves prior behavior.
    /// </param>
    /// <returns>A <see cref="DeviceProxy"/>. Dispose when done.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tracker"/> is <see langword="null"/>.
    /// </exception>
    public static DeviceProxy Create(
        DeviceTracker tracker,
        Func<DeviceInfo, CancellationToken, Task>? onActivated = null,
        Func<DeviceInfo, Task>? onDeactivated = null,
        Func<DeviceInfo, CancellationToken, Task>? whileOpen = null,
        IRecoveryPolicy? recoveryPolicy = null,
        IDeviceReset? deviceReset = null,
        IResetSafetyGate? resetSafetyGate = null,
        bool faultedNodeRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        var handle = new DeviceProxy(
            tracker, onActivated, onDeactivated, whileOpen,
            recoveryPolicy, deviceReset, resetSafetyGate, faultedNodeRecovery);
        handle.CheckInitialState();
        return handle;
    }
}
