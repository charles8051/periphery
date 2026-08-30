// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Periphery;

/// <summary>
/// Dynamically tracks all devices matching a filter, creating a child
/// <see cref="DeviceTracker"/> for each unique <see cref="DeviceInfo.Id"/>
/// as devices appear. Child trackers are persistent — they survive
/// disconnect and disappear cycles so that consumers holding references
/// (e.g., via <see cref="DeviceProxy"/> or <see cref="DeviceSessionHost{TSession}"/>)
/// retain their reconnect path.
///
/// <para>Register with a <see cref="DeviceWatcher"/> via
/// <see cref="DeviceWatcher.AddMultiTracker(Action{DeviceFilter}, string?)"/>
/// before calling <see cref="DeviceWatcher.StartAsync"/>.</para>
///
/// <para>Implements <see cref="IObservable{T}"/> of
/// <see cref="DeviceTrackerState"/>. Subscribers receive state changes
/// from <em>any</em> child tracker — use
/// <see cref="DeviceTrackerState.Device"/> to identify which device
/// changed.</para>
/// </summary>
/// <remarks>
/// <para><b>Lifecycle:</b> The group decides when to <em>start</em>
/// tracking a device (first appearance). The group never automatically
/// removes a child tracker — child trackers transition through
/// <see cref="DeviceActivityStatus.Absent"/> /
/// <see cref="DeviceActivityStatus.Present"/> /
/// <see cref="DeviceActivityStatus.Active"/> like any normal
/// <see cref="DeviceTracker"/>.</para>
///
/// <para><b>Threading:</b> All events fire on thread-pool threads.
/// UI dispatch is the consumer's responsibility.</para>
/// </remarks>
public sealed class MultiDeviceTracker : IObservable<DeviceTrackerState>
{
    private readonly DeviceFilter _filter;
    private readonly ConcurrentDictionary<DeviceId, DeviceTracker> _children = new();
    private readonly List<IObserver<DeviceTrackerState>> _observers = [];
    private readonly object _observerLock = new();

    private DeviceWatcher? _owner;

    /// <summary>
    /// Creates a group tracker with fluent filter configuration.
    /// The tracker starts unbound — pass it to
    /// <see cref="DeviceWatcher.AddMultiTracker(MultiDeviceTracker)"/>
    /// or use the factory overload to activate.
    /// </summary>
    /// <param name="configure">Configures the filter criteria for the group.</param>
    /// <param name="name">Optional human-readable label.</param>
    public MultiDeviceTracker(Action<DeviceFilter> configure, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _filter = new DeviceFilter();
        configure(_filter);
        if (!_filter.HasAnyCriteria)
            throw new ArgumentException(
                "The configure delegate must set at least one filter criterion. " +
                "A group tracker with no criteria would match every device.",
                nameof(configure));
        Name = name;
    }

    internal MultiDeviceTracker(DeviceFilter filter, string? name = null)
    {
        _filter = filter;
        Name = name;
    }

    // ── Public state ───────────────────────────────────────────────────

    /// <summary>Optional human-readable label for this group tracker.</summary>
    public string? Name { get; }

    /// <summary>
    /// All child trackers, keyed by <see cref="DeviceInfo.Id"/>.
    /// The dictionary grows monotonically as new devices are seen.
    /// Each child is a standard <see cref="DeviceTracker"/> with full
    /// lifecycle semantics.
    /// </summary>
    public IReadOnlyDictionary<DeviceId, DeviceTracker> Trackers => _children;

    /// <summary>Number of child trackers (ever-seen devices).</summary>
    public int Count => _children.Count;

    /// <summary><c>true</c> when at least one child tracker exists.</summary>
    public bool HasAny => !_children.IsEmpty;

    // ── Events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a new device is seen for the first time and a child
    /// <see cref="DeviceTracker"/> is created. The tracker is already
    /// populated with the device's initial state.
    /// </summary>
    public event EventHandler<DeviceTracker>? DeviceAdded;

    // ── IObservable<DeviceTrackerState> ────────────────────────────────

    /// <summary>
    /// Subscribe to state changes from any child tracker.
    /// Immediately delivers the current state of all existing children,
    /// then pushes a snapshot on every subsequent child state change.
    /// </summary>
    public IDisposable Subscribe(IObserver<DeviceTrackerState> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        DeviceTrackerState[] currentStates;
        lock (_observerLock)
        {
            _observers.Add(observer);
            currentStates = _children.Values
                .Select(t => t.CurrentState)
                .ToArray();
        }

        foreach (var state in currentStates)
        {
            try { observer.OnNext(state); }
            catch (Exception ex)
            {
                lock (_observerLock) _observers.Remove(observer);
                try { observer.OnError(ex); } catch { }
                return new Unsubscriber(this, observer);
            }
        }

        return new Unsubscriber(this, observer);
    }

    // ── Internal — ownership ───────────────────────────────────────────

    internal void Bind(DeviceWatcher owner)
    {
        if (_owner is not null)
            throw new InvalidOperationException(
                "This MultiDeviceTracker is already bound to an active DeviceWatcher. " +
                "Dispose the current watcher before re-attaching the group tracker.");
        _owner = owner;
    }

    internal void Unbind()
    {
        foreach (var child in _children.Values)
        {
            child.StateChanged -= OnChildStateChanged;
            child.Unbind();
        }
        _children.Clear();
        _owner = null;
    }

    // ── Internal — filter check ────────────────────────────────────────

    internal bool Matches(DeviceInfo device) => _filter.Matches(device);

    // ── Internal — state updates (called by DeviceWatcher) ─────────────

    internal void OnDeviceAppeared(DeviceInfo device)
    {
        if (!_filter.Matches(device)) return;
        var child = GetOrCreateChild(device);
        child.OnDeviceAppeared(device);
    }

    internal void OnDeviceActivated(DeviceInfo device)
    {
        if (!_filter.Matches(device)) return;
        var child = GetOrCreateChild(device);
        child.OnDeviceConnected(device);
    }

    internal void OnDeviceDeactivated(DeviceInfo device)
    {
        if (!_filter.Matches(device)) return;
        if (_children.TryGetValue(device.Id, out var child))
            child.OnDeviceDisconnected(device);
    }

    internal void OnDeviceDisappeared(DeviceInfo device)
    {
        if (!_filter.Matches(device)) return;
        if (_children.TryGetValue(device.Id, out var child))
            child.OnDeviceDisappeared(device);
    }

    internal void OnDevicePropertyChanged(DeviceInfo previous, DeviceInfo current, IReadOnlySet<string> changedProperties)
    {
        if (!_filter.Matches(current)) return;
        if (_children.TryGetValue(current.Id, out var child))
            child.OnDevicePropertyChanged(previous, current, changedProperties);
    }

    // ── Private ────────────────────────────────────────────────────────

    private DeviceTracker GetOrCreateChild(DeviceInfo device)
    {
        if (_children.TryGetValue(device.Id, out var existing))
            return existing;

        // Create a child tracker with the group filter + exact ID match.
        // The child uses a DeviceFilter that accepts only this specific device.
        var childFilter = new DeviceFilter();
        _filter.CopyTo(childFilter);
        childFilter.WithId(device.Id);

        var child = new DeviceTracker(childFilter, device.Name ?? device.Id);
        child.Bind(_owner!);
        child.StateChanged += OnChildStateChanged;

        if (!_children.TryAdd(device.Id, child))
        {
            // Another thread beat us — use theirs, discard ours.
            child.StateChanged -= OnChildStateChanged;
            child.Unbind();
            return _children[device.Id];
        }

        DeviceAdded?.Invoke(this, child);
        return child;
    }

    private void OnChildStateChanged(object? sender, DeviceTrackerState state)
    {
        IObserver<DeviceTrackerState>[] snapshot;
        lock (_observerLock) snapshot = [.. _observers];
        foreach (var observer in snapshot)
        {
            try { observer.OnNext(state); }
            catch (Exception ex)
            {
                lock (_observerLock) _observers.Remove(observer);
                try { observer.OnError(ex); } catch { }
            }
        }
    }

    private sealed class Unsubscriber(MultiDeviceTracker group, IObserver<DeviceTrackerState> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (group._observerLock) group._observers.Remove(observer);
        }
    }
}
