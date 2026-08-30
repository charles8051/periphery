// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Periphery;

/// <summary>
/// Tracks the presence and connection state of a device (or priority-ordered
/// set of candidate devices) against one or more <see cref="DeviceProfile"/>s.
/// Created via <see cref="DeviceWatcher.AddTracker(Action{DeviceFilter}, string?)"/> or
/// constructed directly with <see cref="DeviceTracker(string, DeviceProfile[])"/>
/// for multi-profile fallback scenarios.
///
/// <para>Exposes two orthogonal state properties:</para>
/// <list type="bullet">
/// <item><see cref="Device"/> — the best-known device snapshot. Non-null whenever
/// a matching device is present or connected.</item>
/// <item><see cref="ActivityStatus"/> — <see cref="DeviceActivityStatus.Absent"/>,
/// <see cref="DeviceActivityStatus.Present"/>, or
/// <see cref="DeviceActivityStatus.Active"/>.
/// For USB devices present and active fire simultaneously;
/// for Bluetooth they diverge (paired but out-of-range = Present, not Active).</item>
/// </list>
///
/// <para>Profiles are evaluated in priority order. The tracker latches to the
/// first device that matches the highest-priority profile. The latch releases
/// automatically when the device disconnects, allowing the next matching device
/// to claim the slot.</para>
///
/// <para>Implements two notification surfaces:</para>
/// <list type="bullet">
/// <item><see cref="StateChanged"/> (<see cref="EventHandler{DeviceTrackerState}"/>) —
/// delivers an atomic <see cref="DeviceTrackerState"/> snapshot; no need to re-read
/// from the tracker.</item>
/// <item><see cref="IObservable{T}"/> of <see cref="DeviceTrackerState"/> — the same
/// snapshot pushed to Rx-compatible subscribers.</item>
/// </list>
///
/// <para>All notifications fire on the thread-pool thread that received the
/// OS event. UI dispatch is the consumer's responsibility.</para>
/// </summary>
/// <remarks>
/// <para><b>Reusability:</b> A tracker survives watcher disposal. Event handlers
/// and <see cref="IObserver{T}"/> subscriptions remain attached. The tracker can
/// be re-attached to a new watcher — at most one active watcher at a time,
/// enforced at runtime.</para>
/// </remarks>
public sealed class DeviceTracker : IDeviceTracker, IObservable<DeviceTrackerState>
{
    private IReadOnlyList<DeviceProfile> _profiles;

    // Pure latch/resolution state (the functional core). Swapped wholesale
    // under _lock by each transition; never mutated in place. See ADR-0052.
    private DeviceTrackerResolution _resolution;

    private readonly List<IObserver<DeviceTrackerState>> _observers = [];
    private readonly object _lock = new();

    private DeviceWatcher? _owner;

    // Resolved view of _resolution, recomputed via the pure core's Resolve()
    // after each transition under _lock. Starts at the Unknown sentinel — a
    // freshly-constructed tracker has not yet been enumerated, so it is Unknown
    // (not Absent) until its first resolve. See ADR-0056.
    private DeviceTrackerState _state =
        new(null, DeviceActivityStatus.Unknown, null);

    /// <summary>
    /// Create a single-profile tracker with fluent filter configuration.
    /// The tracker starts unbound — pass it to
    /// <see cref="DeviceWatcher.AddTrackers(DeviceTracker[])"/> to activate.
    /// </summary>
    /// <param name="configure">Configures the filter criteria.</param>
    /// <param name="name">Optional human-readable label (for diagnostics, UI binding, config keys).</param>
    /// <example>
    /// <b>Code-first:</b>
    /// <code>
    /// var mouse = new DeviceTracker(
    ///     t => t.OfCategory(DeviceCategory.Usb).WithUsbId("046D", "C52B"),
    ///     name: "PrimaryMouse");
    /// mouse.StateChanged += (_, _) => UpdateDashboard();
    ///
    /// await using var watcher = Devices.Watch().AddTrackers(mouse);
    /// await watcher.StartAsync();
    /// </code>
    /// <b>Configuration-driven (IOptions + IHostedService):</b>
    /// <code>
    /// // In your DTO:
    /// public DeviceTracker ToTracker() => new(filter =>
    /// {
    ///     if (Category.HasValue) filter.OfCategory(Category.Value);
    ///     if (VendorId is not null) filter.WithUsbId(VendorId, ProductId);
    /// }, name: Name);
    ///
    /// // In your hosted service constructor:
    /// _trackers = options.Value.Devices.Select(d => d.ToTracker()).ToArray();
    /// foreach (var t in _trackers) t.StateChanged += OnStateChanged;
    ///
    /// // In StartAsync:
    /// _watcher = Devices.Watch().AddTrackers(_trackers);
    /// await _watcher.StartAsync(ct);
    /// </code>
    /// </example>
    public DeviceTracker(Action<DeviceFilter> configure, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Name = name;
        _profiles = [new DeviceProfile(configure)];
        InitProfileDictionaries();
    }

    /// <summary>
    /// Create a multi-profile tracker. Profiles are evaluated in priority order;
    /// the highest-priority profile with exactly one connected device wins.
    /// </summary>
    /// <param name="name">Optional human-readable label for this tracker.</param>
    /// <param name="profiles">
    /// One or more profiles in descending priority order. The first profile is
    /// the primary candidate; subsequent profiles are fallbacks.
    /// </param>
    /// <example>
    /// <code>
    /// var mouse = new DeviceTracker("Mouse",
    ///     new DeviceProfile(f => f.WithUsbId("046D", "C52B"), name: "MX Master"),
    ///     new DeviceProfile(f => f.WithUsbId("046D", "C534"), name: "M705"),
    ///     new DeviceProfile(f => f.WithName("USB Input Device"), name: "Dev HID"));
    ///
    /// mouse.StateChanged += (_, state) =>
    /// {
    ///     if (state.Device is { } d)
    ///         Use(d, state.ActiveProfile!.Name);
    /// };
    /// </code>
    /// </example>
    public DeviceTracker(string? name, params DeviceProfile[] profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Length == 0)
            throw new ArgumentException("At least one profile is required.", nameof(profiles));
        foreach (var p in profiles)
            ArgumentNullException.ThrowIfNull(p);
        Name = name;
        _profiles = profiles.ToArray();
        InitProfileDictionaries();
    }

    internal DeviceTracker(DeviceFilter filter, string? name = null)
    {
        Name = name;
        _profiles = [new DeviceProfile(filter)];
        InitProfileDictionaries();
    }

    [MemberNotNull(nameof(_resolution))]
    private void InitProfileDictionaries()
    {
        _resolution = DeviceTrackerResolution.Create(_profiles);
    }

    // ── Public state ───────────────────────────────────────────────────

    /// <summary>
    /// Optional human-readable label for this tracker.
    /// Set at construction; useful for diagnostics, UI binding, and
    /// mapping back to configuration keys.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// The best-known device snapshot — non-null whenever a matching device is
    /// <see cref="DeviceActivityStatus.Present"/> or
    /// <see cref="DeviceActivityStatus.Active"/>.
    /// Use <see cref="ActivityStatus"/> to distinguish between the two.
    /// </summary>
    public DeviceInfo? Device
    {
        get { lock (_lock) return _state.Device; }
    }

    /// <summary>
    /// The resolved activity status of the tracked device.
    /// </summary>
    public DeviceActivityStatus ActivityStatus
    {
        get { lock (_lock) return _state.ActivityStatus; }
    }

    /// <summary>
    /// <c>true</c> when <see cref="ActivityStatus"/> is
    /// <see cref="DeviceActivityStatus.Active"/>.
    /// </summary>
    public bool IsActive
    {
        get { lock (_lock) return _state.IsActive; }
    }

    /// <summary>
    /// <c>true</c> when <see cref="Device"/> is non-null — a matching device is
    /// known to the OS (paired, installed, or plugged in).
    /// </summary>
    public bool IsPresent
    {
        get { lock (_lock) return _state.IsPresent; }
    }

    /// <summary>
    /// The <see cref="DeviceProfile"/> that produced the current resolved state.
    /// Non-null whenever <see cref="Device"/> is non-null. <c>null</c> when nothing matches.
    /// </summary>
    public DeviceProfile? ActiveProfile
    {
        get { lock (_lock) return _state.ActiveProfile; }
    }

    // ── Events

    /// <summary>Raised when <see cref="Device"/>, <see cref="ActivityStatus"/>,
    /// or <see cref="ActiveProfile"/> changes.
    /// argument is an atomic snapshot captured at the moment of the transition —
    /// no need to re-read from the tracker.</summary>
    public event EventHandler<DeviceTrackerState>? StateChanged;

    /// <summary>A matching device entered the OS device tree (installed, paired, plugged in).
    /// Fires for every known device during the initial watcher snapshot.</summary>
    public event EventHandler<DeviceTrackerTransition>? Appeared;

    /// <summary>A matching device left the OS device tree (uninstalled, unpaired, unplugged).
    /// <see cref="DeviceTrackerTransition.Before"/> carries the last-known snapshot.</summary>
    public event EventHandler<DeviceTrackerTransition>? Disappeared;

    /// <summary>A matching device became physically active (driver started, hardware present
    /// and working). For USB devices fires simultaneously with <see cref="Appeared"/>;
    /// for Bluetooth fires when the device comes into range.</summary>
    public event EventHandler<DeviceTrackerTransition>? Activated;

    /// <summary>A matching device became physically inactive (driver stopped, hardware
    /// disconnected). <see cref="DeviceTrackerTransition.Before"/> carries the last-known
    /// snapshot. Also fires as a cascade when an active device disappears.</summary>
    public event EventHandler<DeviceTrackerTransition>? Deactivated;

    /// <summary>
    /// Raised when one or more properties on the resolved <see cref="Device"/> change
    /// value between OS-delivered modification events. Only fires when a device is
    /// resolved — no event when <see cref="Device"/> is <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Complementary to the edge events: <see cref="Activated"/>/<see cref="Deactivated"/>
    /// fire on lifecycle transitions; this event fires when the resolved device's data
    /// changes while it remains in the same lifecycle state.
    /// </remarks>
    public event EventHandler<DevicePropertyChangedEventArgs>? PropertyChanged;

    /// <summary>
    /// Subscribe to state transitions.
    /// Immediately delivers the current <see cref="DeviceTrackerState"/> snapshot to
    /// the new observer, then pushes a fresh snapshot on every subsequent change to
    /// <see cref="Device"/>, <see cref="ActivityStatus"/>, or <see cref="ActiveProfile"/>.
    /// This makes <see cref="DeviceTracker"/> behave like a BehaviorSubject
    /// subscriber always sees the current state without waiting for the next event.
    /// <code>
    /// tracker.Subscribe(s => Console.WriteLine(s.Device?.Name));
    /// // IsActive-only, Rx-style:
    /// tracker.Select(s => s.IsActive).DistinctUntilChanged().Subscribe(...);
    /// </code>
    /// </summary>
    public IDisposable Subscribe(IObserver<DeviceTrackerState> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        DeviceTrackerState current;
        lock (_lock)
        {
            _observers.Add(observer);
            current = _state;
        }
        try
        {
            observer.OnNext(current);
        }
        catch (Exception ex)
        {
            lock (_lock) _observers.Remove(observer);
            try { observer.OnError(ex); } catch { }
        }
        return new Unsubscriber(this, observer);
    }

    // ── Runtime reconfigure (ADR-0046) ─────────────────────────────────

    /// <summary>
    /// Atomically replace this tracker's filter with a new single-profile
    /// configuration. The tracker's identity, event handlers, and
    /// <see cref="IObserver{T}"/> subscriptions are preserved across the
    /// swap. After replacement, the tracker re-evaluates against its
    /// owning <see cref="DeviceWatcher"/>'s current device snapshot;
    /// <see cref="StateChanged"/> fires <em>once</em> with the new
    /// resolved state if (and only if) the resolved device or activity
    /// status changed.
    /// </summary>
    /// <param name="configure">Configures the new filter criteria.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The configure delegate doesn't set any filter criteria
    /// (would match every device).
    /// </exception>
    /// <remarks>
    /// <para>
    /// Symmetric counterpart of the
    /// <see cref="DeviceTracker(Action{DeviceFilter}, string?)"/>
    /// constructor — same vocabulary, same validation, just at a later
    /// point in time.
    /// </para>
    /// <para>
    /// Calling on an unbound tracker (one not yet attached to a
    /// <see cref="DeviceWatcher"/>) is legal — the new filter takes
    /// effect at the next <see cref="DeviceWatcher.AddTracker(DeviceTracker)"/>
    /// + <see cref="DeviceWatcher.StartAsync"/> sequence.
    /// </para>
    /// </remarks>
    public void Reconfigure(Action<DeviceFilter> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var newFilter = new DeviceFilter();
        configure(newFilter);

        if (!newFilter.HasAnyCriteria)
            throw new ArgumentException(
                "The configure delegate must set at least one filter criterion. " +
                "A tracker with no criteria would match every device.",
                nameof(configure));

        ApplyProfiles([new DeviceProfile(newFilter)]);
    }

    /// <summary>
    /// Atomically replace this tracker's profile list with a new
    /// multi-profile configuration. Semantics match <see cref="Reconfigure"/>
    /// — identity preserved, single batched <see cref="StateChanged"/>
    /// emission, immediate re-evaluation against the owning watcher's
    /// device cache.
    /// </summary>
    /// <param name="profiles">
    /// One or more profiles in descending priority order, same shape as
    /// the multi-profile constructor.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="profiles"/> is <c>null</c>, or any element is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="profiles"/> is empty.
    /// </exception>
    public void ReplaceProfiles(params DeviceProfile[] profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Length == 0)
            throw new ArgumentException("At least one profile is required.", nameof(profiles));
        foreach (var p in profiles)
            ArgumentNullException.ThrowIfNull(p);

        ApplyProfiles(profiles.ToArray());
    }

    /// <summary>
    /// Shared apply path for the two public reconfigure entry points.
    /// Holds <see cref="_lock"/> for the duration of the swap +
    /// re-evaluation so any concurrent OS device event serialises
    /// behind the reconfigure (and applies on top of the new state
    /// when its turn comes).
    /// </summary>
    private void ApplyProfiles(IReadOnlyList<DeviceProfile> newProfiles)
    {
        DeviceTrackerState before, after;
        DeviceWatcher? owner;
        lock (_lock)
        {
            before = _state;

            _profiles = newProfiles;
            InitProfileDictionaries();
            owner = _owner;

            // Replay the watcher's known-device snapshot through the
            // new filter. While we hold _lock, real OS device events
            // can't interleave — they queue on this same lock and
            // apply after we release.
            owner?.ReplayKnownDevicesTo(this);

            _state = _resolution.Resolve();
            after = _state;
        }
        NotifyChanges(before, after);
    }

    /// <summary>
    /// Replay-time variant of <see cref="OnDeviceAppeared"/> +
    /// <see cref="OnDeviceConnected"/> rolled into one. Called by
    /// <see cref="DeviceWatcher.ReplayKnownDevicesTo"/> for each device
    /// in its cache during a <see cref="Reconfigure"/> /
    /// <see cref="ReplaceProfiles"/>. Must be called under
    /// <see cref="_lock"/> (the calling apply path holds it).
    /// </summary>
    /// <remarks>
    /// Doesn't call <see cref="NotifyChanges"/> per device — the
    /// apply path captures before/after state and notifies once for
    /// the net transition. That makes a reconfigure observable as a
    /// single transition rather than an N-event fan-out.
    /// </remarks>
    internal void ReplayDeviceInternal(DeviceInfo device)
    {
        _resolution = _resolution.ApplyReplay(device);
    }

    // ── Internal — ownership ───────────────────────────────────────────

    /// <summary>Bind this tracker to a watcher. Throws if already bound to an active watcher.</summary>
    internal void Bind(DeviceWatcher owner)
    {
        lock (_lock)
        {
            if (_owner is not null)
                throw new InvalidOperationException(
                    "This DeviceTracker is already bound to an active DeviceWatcher. " +
                    "Dispose the current watcher before re-attaching the tracker.");
            _owner = owner;
        }
    }

    /// <summary>Unbind from the owning watcher and reset all state to inert.</summary>
    internal void Unbind()
    {
        DeviceTrackerState before;
        lock (_lock)
        {
            before = _state;
            // Reset the pure core to inert; force the resolved view to Absent
            // (post-determination teardown — never back to Unknown). The empty
            // resolution resolves to Absent anyway, but stating it is the
            // contract: Unbind always lands on Absent.
            InitProfileDictionaries();
            _state = new DeviceTrackerState(null, DeviceActivityStatus.Absent, null);
            _owner = null;
        }
        NotifyChanges(before, _state);
    }

    // ── Internal — state updates (called by DeviceWatcher) ─────────────

    internal void OnDeviceAppeared(DeviceInfo device)
    {
        DeviceTrackerState before, after;
        lock (_lock)
        {
            before = _state;
            _resolution = _resolution.ApplyAppeared(device);
            _state = _resolution.Resolve();
            after = _state;
        }
        NotifyChanges(before, after);
    }

    internal void OnDeviceDisappeared(DeviceInfo device)
    {
        DeviceTrackerState before, after;
        lock (_lock)
        {
            before = _state;
            _resolution = _resolution.ApplyDisappeared(device);
            _state = _resolution.Resolve();
            after = _state;
        }
        NotifyChanges(before, after);
    }

    internal void OnDeviceConnected(DeviceInfo device)
    {
        DeviceTrackerState before, after;
        lock (_lock)
        {
            before = _state;
            _resolution = _resolution.ApplyConnected(device);
            _state = _resolution.Resolve();
            after = _state;
        }
        NotifyChanges(before, after);
    }

    internal void OnDeviceDisconnected(DeviceInfo device)
    {
        DeviceTrackerState before, after;
        lock (_lock)
        {
            before = _state;
            _resolution = _resolution.ApplyDisconnected(device);
            _state = _resolution.Resolve();
            after = _state;
        }
        NotifyChanges(before, after);
    }

    /// <summary>
    /// Signal that the owning watcher's initial device enumeration has settled.
    /// Called once per watcher start, after the snapshot fan-out has run
    /// (<see cref="DeviceWatcher.SnapshotCurrentDevicesAsync"/>). Resolves a
    /// tracker that is still <see cref="DeviceActivityStatus.Unknown"/> — i.e.
    /// one the snapshot matched nothing for — to its determined state
    /// (<see cref="DeviceActivityStatus.Absent"/> for the genuinely-absent case),
    /// emitting the single <c>Unknown → Absent</c> transition.
    /// </summary>
    /// <remarks>
    /// <para>Idempotent and one-shot in effect: a tracker the fan-out already
    /// resolved (status no longer <see cref="DeviceActivityStatus.Unknown"/>)
    /// is left untouched — the early return guards the race where a matched
    /// tracker's status was set during the same snapshot, so the hook never
    /// re-emits for an already-determined tracker.</para>
    /// <para>Mirrors the <see cref="Unbind"/> shape: capture before-state,
    /// mutate under <see cref="_lock"/>, notify exactly once outside the lock.</para>
    /// </remarks>
    internal void OnInitialEnumerationComplete()
    {
        DeviceTrackerState before, after;
        lock (_lock)
        {
            // Already determined by the snapshot fan-out (matched tracker) —
            // no-op, no spurious re-emit.
            if (_state.ActivityStatus != DeviceActivityStatus.Unknown) return;

            before = _state;
            _state = _resolution.Resolve();   // Unknown -> Absent: latches are empty for an unmatched tracker
            after = _state;
        }
        NotifyChanges(before, after);
    }

    internal bool Matches(DeviceInfo device) => _profiles.Any(p => p.Filter.Matches(device));

    internal void OnDevicePropertyChanged(DeviceInfo previous, DeviceInfo current, IReadOnlySet<string> changedProperties)
    {
        DeviceTrackerState before, after;
        bool affectsResolvedDevice;
        lock (_lock)
        {
            before = _state;
            affectsResolvedDevice = _state.Device?.Id == current.Id;

            if (affectsResolvedDevice)
            {
                _resolution = _resolution.ApplyPropertyChanged(current);
                _state = _resolution.Resolve();
            }

            after = _state;
        }
        var args = affectsResolvedDevice
            ? new DevicePropertyChangedEventArgs(previous, current, changedProperties)
            : null;
        NotifyChanges(before, after, args);
    }

    // ── Private — notification ─────────────────────────────────────────

    private void NotifyChanges(DeviceTrackerState before, DeviceTrackerState after,
        DevicePropertyChangedEventArgs? propertyChangedArgs = null)
    {
        bool stateChanged = before.Device != after.Device
            || before.ActivityStatus != after.ActivityStatus
            || !ReferenceEquals(before.ActiveProfile, after.ActiveProfile);

        if (stateChanged)
        {
            StateChanged?.Invoke(this, after);
            IObserver<DeviceTrackerState>[] snapshot;
            lock (_lock) snapshot = [.. _observers];
            foreach (var observer in snapshot)
            {
                try
                {
                    observer.OnNext(after);
                }
                catch (Exception ex)
                {
                    lock (_lock) _observers.Remove(observer);
                    try { observer.OnError(ex); } catch { }
                }
            }

            var transition = new DeviceTrackerTransition(before, after);
            if (!before.IsPresent && after.IsPresent)   Appeared?.Invoke(this, transition);
            if (before.IsPresent && !after.IsPresent)   Disappeared?.Invoke(this, transition);
            if (!before.IsActive && after.IsActive)     Activated?.Invoke(this, transition);
            if (before.IsActive && !after.IsActive)     Deactivated?.Invoke(this, transition);
        }

        if (propertyChangedArgs is not null)
            PropertyChanged?.Invoke(this, propertyChangedArgs);
    }

    private sealed class Unsubscriber(DeviceTracker tracker, IObserver<DeviceTrackerState> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (tracker._lock) tracker._observers.Remove(observer);
        }
    }

    /// <summary>
    /// An atomic snapshot of the tracker's current resolved state.
    /// Equivalent to the value a new subscriber receives immediately on
    /// <see cref="Subscribe"/>.
    /// </summary>
    public DeviceTrackerState CurrentState
    {
        get { lock (_lock) return _state; }
    }
}
