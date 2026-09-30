// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Periphery.Linux.DBus.Core;
using BlueZObjects = System.Collections.Immutable.ImmutableDictionary<string,
    System.Collections.Immutable.ImmutableDictionary<string,
        System.Collections.Immutable.ImmutableDictionary<string, Periphery.Linux.DBus.Core.DBusValue>>>;

namespace Periphery.Linux.BlueZ.Core;

/// <summary>An edge the watch raises for one bonded device.</summary>
internal enum BlueZEdgeKind
{
    Appeared,
    Disappeared,
    Activated,
    Deactivated,
    PropertyChanged,
}

/// <summary>One edge. <see cref="Previous"/> is set for <see cref="BlueZEdgeKind.PropertyChanged"/>.</summary>
internal readonly record struct BlueZEdge(BlueZEdgeKind Kind, DeviceInfo Device, DeviceInfo? Previous = null);

/// <summary>What the watch asks its shell to do.</summary>
internal abstract record BlueZEffect;

/// <summary>Send <c>GetManagedObjects</c> to <paramref name="Destination"/>, then report its serial.</summary>
internal sealed record RequestSnapshot(string Destination) : BlueZEffect;

/// <summary>Log <paramref name="Message"/>, once per <paramref name="Key"/>.</summary>
internal sealed record ReportProblem(string Key, LogLevel Level, string Message) : BlueZEffect;

/// <summary>An input to <see cref="BlueZWatch.Step"/>.</summary>
internal abstract record BlueZObservation;

/// <summary>The shell has subscribed. <paramref name="Owner"/> is <c>org.bluez</c>'s unique name, if any.</summary>
internal sealed record WatchStarted(string? Owner) : BlueZObservation;

/// <summary>The shell sent the requested snapshot under <paramref name="Serial"/>.</summary>
internal sealed record SnapshotSent(uint Serial) : BlueZObservation;

/// <summary>A message arrived on the connection.</summary>
internal sealed record MessageReceived(DBusMessage Message) : BlueZObservation;

/// <summary>The wake time <see cref="BlueZWatch.NextWake"/> named has come.</summary>
internal sealed record WakeTime : BlueZObservation;

/// <summary>The connection to the bus is gone and will not come back.</summary>
internal sealed record ConnectionLost(string Reason) : BlueZObservation;

/// <summary>An outstanding <c>GetManagedObjects</c>. <see cref="Serial"/> is null until it is sent.</summary>
internal sealed record PendingSnapshot(string Destination, uint? Serial, DateTimeOffset Deadline);

/// <summary>Everything the watch knows. Nothing here refers to a connection or a clock.</summary>
internal sealed record BlueZWatchState
{
    public static BlueZWatchState Initial { get; } = new();

    /// <summary><c>org.bluez</c>'s unique name, or null while nobody owns it.</summary>
    public string? Owner { get; init; }

    public PendingSnapshot? Pending { get; init; }

    /// <summary>True once the first snapshot is applied, or has failed. Edges are silent until then.</summary>
    public bool Seeded { get; init; }

    /// <summary>True after <c>AccessDenied</c> or a lost connection. Nothing more is asked.</summary>
    public bool Stopped { get; init; }

    public DateTimeOffset? RetryAt { get; init; }

    public TimeSpan RetryDelay { get; init; } = BlueZWatch.FirstRetry;

    /// <summary>BlueZ's objects as the owner last described them: path, interface, property.</summary>
    public BlueZObjects Objects { get; init; } = BlueZObjects.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The bonded devices those objects map to, by <see cref="DeviceInfo.Id"/>.</summary>
    public ImmutableDictionary<string, DeviceInfo> Devices { get; init; } =
        ImmutableDictionary<string, DeviceInfo>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The result of one <see cref="BlueZWatch.Step"/>.</summary>
internal readonly record struct BlueZStep(BlueZWatchState State, ImmutableArray<BlueZEdge> Edges, ImmutableArray<BlueZEffect> Effects);

/// <summary>
/// ADR-0091 D5, D6 and D8 as a pure state machine. It keeps BlueZ's objects as the owner reports
/// them, maps them to bonded devices, and raises an edge for each difference between one mapping and
/// the next. Signals that arrive while a snapshot is outstanding describe changes the snapshot
/// already holds, because BlueZ flushes its signals before any reply, so they are dropped.
/// </summary>
internal static class BlueZWatch
{
    internal const string BusName = "org.freedesktop.DBus";
    internal const string BusPath = "/org/freedesktop/DBus";
    internal const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    internal static readonly TimeSpan SnapshotDeadline = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);

    // The Device1 properties D4 reads. BlueZ never invalidates these; if it does, re-read.
    private static readonly ImmutableHashSet<string> ReadProperties =
        ["Address", "Adapter", "Alias", "Name", "Paired", "Bonded", "Connected"];

    /// <summary>When the shell must call <see cref="Step"/> with <see cref="WakeTime"/>, if ever.</summary>
    internal static DateTimeOffset? NextWake(BlueZWatchState state) =>
        (state.Pending?.Deadline, state.RetryAt) switch
        {
            (null, null) => null,
            (DateTimeOffset d, null) => d,
            (null, DateTimeOffset r) => r,
            (DateTimeOffset d, DateTimeOffset r) => d < r ? d : r,
        };

    internal static BlueZStep Step(BlueZWatchState state, BlueZObservation observation, DateTimeOffset now)
    {
        var effects = ImmutableArray.CreateBuilder<BlueZEffect>();
        var next = observation switch
        {
            WatchStarted started => Start(state, started.Owner, now, effects),
            SnapshotSent sent => state.Pending is { Serial: null } pending
                ? state with { Pending = pending with { Serial = sent.Serial } }
                : state,
            MessageReceived received => Receive(state, received.Message, now, effects),
            WakeTime => Wake(state, now, effects),
            ConnectionLost lost => Lose(state, effects, "ConnectionLost", LogLevel.Warning,
                $"The system bus connection dropped ({lost.Reason}). Bluetooth devices are no longer watched.") with { Stopped = true },
            _ => state,
        };
        return Finish(state, next, effects);
    }

    // Maps the objects, and turns the difference from the previous mapping into edges. Before the
    // seed completes the watch is silent: the watcher's own enumeration announces those devices.
    private static BlueZStep Finish(BlueZWatchState before, BlueZWatchState after, ImmutableArray<BlueZEffect>.Builder effects)
    {
        var devices = ReferenceEquals(before.Objects, after.Objects)
            ? after.Devices
            : BlueZInventory.Map(after.Objects).ToImmutableDictionary(d => d.Id.Value, StringComparer.OrdinalIgnoreCase);
        if (!ReferenceEquals(devices, after.Devices))
            after = after with { Devices = devices };

        var edges = before.Seeded ? Diff(before.Devices, devices) : ImmutableArray<BlueZEdge>.Empty;
        return new BlueZStep(after, edges, effects.ToImmutable());
    }

    private static BlueZWatchState Start(BlueZWatchState state, string? owner, DateTimeOffset now, ImmutableArray<BlueZEffect>.Builder effects)
    {
        state = state with { Owner = owner };
        if (owner is null)
        {
            effects.Add(new ReportProblem("Absent", LogLevel.Information,
                "BlueZ is not running. Bluetooth devices are watched from when it starts."));
            return state with { Seeded = true };
        }
        return Request(state, owner, now, effects);
    }

    private static BlueZWatchState Request(BlueZWatchState state, string owner, DateTimeOffset now, ImmutableArray<BlueZEffect>.Builder effects)
    {
        effects.Add(new RequestSnapshot(owner));
        return state with { Pending = new PendingSnapshot(owner, null, now + SnapshotDeadline), RetryAt = null };
    }

    private static BlueZWatchState Receive(BlueZWatchState state, DBusMessage message, DateTimeOffset now, ImmutableArray<BlueZEffect>.Builder effects)
    {
        if (state.Stopped)
            return state;

        if (IsOwnerChange(message, out string? newOwner))
        {
            if (newOwner == state.Owner)
                return state;
            state = state.Owner is null
                ? state with { Pending = null, RetryAt = null }
                : Lose(state, effects, null, LogLevel.Information, null);
            state = state with { Owner = newOwner };
            return newOwner is null ? state : Request(state, newOwner, now, effects);
        }

        if (message.Type is DBusMessageType.MethodReturn or DBusMessageType.Error)
            return IsSnapshotReply(state, message) ? ApplyReply(state, message, now, effects) : state;

        if (message.Type != DBusMessageType.Signal || state.Owner is null || message.Sender != state.Owner)
            return state;

        // D8: a signal sent before the snapshot's reply describes a change the reply already holds.
        if (state.Pending is not null)
            return state;

        return message switch
        {
            { Interface: BlueZInventory.ObjectManagerInterface, Member: "InterfacesAdded", Signature: "oa{sa{sv}}" }
                => state with { Objects = AddInterfaces(state.Objects, message.Body) },
            { Interface: BlueZInventory.ObjectManagerInterface, Member: "InterfacesRemoved", Signature: "oas" }
                => state with { Objects = RemoveInterfaces(state.Objects, message.Body) },
            { Interface: PropertiesInterface, Member: "PropertiesChanged", Signature: "sa{sv}as", Path: { } path }
                => ChangeProperties(state, path, message.Body, now, effects),
            _ => state,
        };
    }

    // D8: only the bus may report an owner change, on its own path, for org.bluez.
    private static bool IsOwnerChange(DBusMessage message, out string? newOwner)
    {
        newOwner = null;
        if (message is not
            {
                Type: DBusMessageType.Signal, Sender: BusName, Path: BusPath, Interface: BusName,
                Member: "NameOwnerChanged", Signature: "sss",
            }
            || message.Body is not [DBusString { Value: BlueZInventory.Service }, _, DBusString { Value: var owner }])
            return false;

        newOwner = owner.Length == 0 ? null : owner;
        return true;
    }

    private static bool IsSnapshotReply(BlueZWatchState state, DBusMessage message) =>
        state.Pending is { Serial: { } serial } pending
        && message.ReplySerial == serial
        && (message.Sender == pending.Destination || message.Sender == BusName);

    private static BlueZWatchState ApplyReply(BlueZWatchState state, DBusMessage message, DateTimeOffset now, ImmutableArray<BlueZEffect>.Builder effects)
    {
        state = state with { Pending = null };

        if (message.Type == DBusMessageType.MethodReturn && message.Signature == BlueZInventory.ManagedObjectsSignature)
        {
            return state with
            {
                Objects = BlueZInventory.ReadObjects(message.Body[0]),
                Seeded = true,
                RetryDelay = FirstRetry,
            };
        }

        string errorName = message.ErrorName ?? $"reply of type '{message.Signature}'";
        var kind = message.Type == DBusMessageType.Error ? BlueZInventory.ClassifyError(errorName) : BlueZFailureKind.Error;
        if (kind == BlueZFailureKind.AccessDenied)
        {
            return Lose(state, effects, errorName, LogLevel.Warning,
                $"The system bus refused this process access to BlueZ ({errorName}). Bluetooth devices are not watched.") with { Stopped = true };
        }

        var level = kind == BlueZFailureKind.Absent ? LogLevel.Information : LogLevel.Warning;
        return Retry(Lose(state, effects, errorName, level,
            $"BlueZ answered with {errorName}. Bluetooth devices are watched again when it answers."), now);
    }

    private static BlueZWatchState Wake(BlueZWatchState state, DateTimeOffset now, ImmutableArray<BlueZEffect>.Builder effects)
    {
        if (state.Pending is { } pending && pending.Deadline <= now)
        {
            state = Retry(Lose(state with { Pending = null }, effects, "Timeout", LogLevel.Warning,
                $"BlueZ did not answer within {SnapshotDeadline.TotalSeconds:0} s. Bluetooth devices are watched again when it answers."), now);
        }

        if (state.RetryAt is { } retryAt && retryAt <= now && state.Pending is null && !state.Stopped && state.Owner is { } owner)
            state = Request(state, owner, now, effects);

        return state;
    }

    // Everything BlueZ described is gone: its devices raise Disappeared. The seed counts as done.
    private static BlueZWatchState Lose(BlueZWatchState state, ImmutableArray<BlueZEffect>.Builder effects, string? key, LogLevel level, string? message)
    {
        if (key is not null && message is not null)
            effects.Add(new ReportProblem(key, level, message));
        return state with
        {
            Pending = null,
            RetryAt = null,
            Seeded = true,
            Objects = state.Objects.Clear(),
        };
    }

    private static BlueZWatchState Retry(BlueZWatchState state, DateTimeOffset now) => state with
    {
        RetryAt = now + state.RetryDelay,
        RetryDelay = state.RetryDelay * 2 < MaxRetry ? state.RetryDelay * 2 : MaxRetry,
    };

    private static BlueZObjects AddInterfaces(BlueZObjects objects, ImmutableArray<DBusValue> body)
    {
        if (body is not [DBusString { Code: 'o', Value: var path }, var interfaces])
            return objects;

        var existing = objects.TryGetValue(path, out var held)
            ? held
            : ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>>.Empty.WithComparers(StringComparer.Ordinal);
        foreach (var (name, properties) in BlueZInventory.ReadInterfaces(interfaces))
            existing = existing.SetItem(name, properties);
        return objects.SetItem(path, existing);
    }

    private static BlueZObjects RemoveInterfaces(BlueZObjects objects, ImmutableArray<DBusValue> body)
    {
        if (body is not [DBusString { Code: 'o', Value: var path }, DBusArray { Items: var names }]
            || !objects.TryGetValue(path, out var held))
            return objects;

        foreach (var name in names)
        {
            if (name is DBusString { Value: var interfaceName })
                held = held.Remove(interfaceName);
        }
        return held.IsEmpty ? objects.Remove(path) : objects.SetItem(path, held);
    }

    private static BlueZWatchState ChangeProperties(
        BlueZWatchState state, string path, ImmutableArray<DBusValue> body, DateTimeOffset now, ImmutableArray<BlueZEffect>.Builder effects)
    {
        if (body is not [DBusString { Value: BlueZInventory.DeviceInterface }, var changed, DBusArray { Items: var invalidated }]
            || !state.Objects.TryGetValue(path, out var interfaces)
            || !interfaces.TryGetValue(BlueZInventory.DeviceInterface, out var properties))
            return state;

        properties = properties.SetItems(BlueZInventory.ReadProperties(changed));
        foreach (var name in invalidated)
        {
            if (name is not DBusString { Value: var property })
                continue;
            if (ReadProperties.Contains(property) && state.Owner is { } owner)
                return Request(state, owner, now, effects);
            properties = properties.Remove(property);
        }

        return state with { Objects = state.Objects.SetItem(path, interfaces.SetItem(BlueZInventory.DeviceInterface, properties)) };
    }

    // D5: gone, then new, then changed, each in Id order.
    private static ImmutableArray<BlueZEdge> Diff(ImmutableDictionary<string, DeviceInfo> before, ImmutableDictionary<string, DeviceInfo> after)
    {
        var edges = ImmutableArray.CreateBuilder<BlueZEdge>();
        foreach (var id in before.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!after.ContainsKey(id))
                edges.Add(new BlueZEdge(BlueZEdgeKind.Disappeared, before[id]));
        }

        foreach (var id in after.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            var current = after[id];
            if (!before.TryGetValue(id, out var previous))
            {
                edges.Add(new BlueZEdge(BlueZEdgeKind.Appeared, current));
                if (current.IsActive)
                    edges.Add(new BlueZEdge(BlueZEdgeKind.Activated, current));
                continue;
            }

            if (DeviceInfoDiff.Compute(previous, current).Count == 0)
                continue;
            if (previous.IsActive != current.IsActive)
                edges.Add(new BlueZEdge(current.IsActive ? BlueZEdgeKind.Activated : BlueZEdgeKind.Deactivated, current));
            edges.Add(new BlueZEdge(BlueZEdgeKind.PropertyChanged, current, previous));
        }
        return edges.ToImmutable();
    }
}
