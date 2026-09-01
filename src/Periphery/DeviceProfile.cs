// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// A named device-matching candidate within a <see cref="DeviceTracker"/>.
/// Multiple profiles are tried in priority order; the highest-priority profile
/// with exactly one connected device determines the tracker's resolved
/// <see cref="DeviceTracker.Device"/>.
/// </summary>
/// <example>
/// <code>
/// var mouse = new DeviceTracker("Mouse",
///     new DeviceProfile(f => f.WithUsbId("046D", "C52B"), name: "MX Master"),
///     new DeviceProfile(f => f.WithUsbId("046D", "C534"), name: "M705"),
///     new DeviceProfile(f => f.WithName("USB Input Device"), name: "Dev HID"));
/// </code>
/// </example>
public sealed class DeviceProfile
{
    /// <summary>
    /// Creates a profile with fluent filter configuration.
    /// </summary>
    /// <param name="configure">Configures the filter criteria for this profile.</param>
    /// <param name="name">
    /// Optional human-readable label, surfaced via
    /// <see cref="DeviceTracker.ActiveProfile"/> for diagnostics and UI display.
    /// </param>
    public DeviceProfile(Action<DeviceFilter> configure, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Filter = new DeviceFilter();
        configure(Filter);
        if (!Filter.HasAnyCriteria)
        {
            var label = name is not null ? $" (profile: \"{name}\")" : "";
            throw new ArgumentException(
                $"The configure delegate must set at least one filter criterion. " +
                $"A profile with no criteria would match every device.{label}",
                nameof(configure));
        }
        Name = name;
    }

    internal DeviceProfile(DeviceFilter filter, string? name = null)
    {
        Filter = filter;
        Name = name;
    }

    /// <summary>
    /// Builds a profile from a bound <see cref="DeviceFilterSpec"/> — the shape
    /// device selection takes when it comes from configuration.
    /// </summary>
    /// <param name="spec">The criteria. Must set at least one.</param>
    /// <param name="name">Optional label, stamped onto <see cref="Name"/>.</param>
    /// <remarks>
    /// A static factory rather than a constructor overload on purpose. The
    /// existing constructor takes <c>Action&lt;DeviceFilter&gt;</c>, and a second
    /// reference-type first parameter would make <c>new DeviceProfile(null)</c>
    /// ambiguous (CS0121) — a source break in exactly the consumers this exists
    /// to help.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The spec sets no criteria, or a value cannot be parsed. The message
    /// carries the spec's description rather than a parameter name, because the
    /// cause is usually a configuration file rather than a call site.
    /// </exception>
    public static DeviceProfile FromSpec(DeviceFilterSpec spec, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!spec.HasAnyCriteria)
        {
            var label = name is not null ? $" (profile: \"{name}\")" : "";
            throw new ArgumentException(
                $"The spec sets no criteria, so it would match every device.{label}",
                nameof(spec));
        }

        return new DeviceProfile(new DeviceFilter().Apply(spec), name);
    }

    /// <summary>Optional human-readable label for this profile.</summary>
    public string? Name { get; }

    internal DeviceFilter Filter { get; }

    /// <summary>
    /// Builds an ID-pinned profile that matches one specific
    /// <see cref="DeviceInfo"/>. Use when you have a concrete device in
    /// hand (e.g. from a UI device picker) and want a tracker that
    /// follows that exact instance across disconnect/reconnect cycles —
    /// rather than constructing a name- or category-based filter that
    /// might also match siblings.
    /// </summary>
    /// <param name="device">The device to pin the profile to. Its <see cref="DeviceInfo.Id"/> drives the match.</param>
    /// <returns>A profile that resolves to <paramref name="device"/> exactly.</returns>
    public static DeviceProfile ForDevice(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceProfile(
            f => f.WithId(device.Id),
            name: device.Name ?? device.Id);
    }
}
