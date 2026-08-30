// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Core enricher that tags sensor devices with <see cref="DeviceTags.Sensor"/>.
/// Replaces the former <c>DeviceCategory.Sensor</c> (ADR-0051): "sensor" is a
/// cross-cutting capability, not an OS subsystem, and the three platforms
/// surface it through three unrelated signals — the Windows <c>Sensor</c> device
/// setup class, the Linux <c>iio</c> (Industrial I/O) subsystem, and the macOS
/// HID sensor usage page (<c>0x20</c>). Each provider populates exactly one of
/// the corresponding <see cref="DeviceInfo"/> fields; this enricher reads
/// whichever is present and emits the tag uniformly.
/// </summary>
/// <remarks>
/// <para><b>ADR-0026 compliant.</b> Pure metadata — reads
/// <see cref="DeviceInfo.ClassGuid"/>, <see cref="DeviceInfo.Subsystem"/>, and
/// <see cref="DeviceInfo.HidUsagePage"/> already populated at enumeration time;
/// opens no handle and performs no device I/O.</para>
/// <para><b>Cross-platform registration.</b> A singleton <see cref="Instance"/>
/// auto-registers with <see cref="DeviceEnrichers"/> via the module initializer
/// below, and the cross-platform <see cref="EnrichmentPipeline"/> (ADR-0051 §5)
/// runs it on Windows, Linux, and macOS alike — so <c>WithTag(DeviceTags.Sensor)</c>
/// works on every platform the old category did.</para>
/// </remarks>
public sealed class SensorEnricher : ITagEmittingEnricher
{
    /// <summary>
    /// Singleton registered with <see cref="DeviceEnrichers"/> by the module
    /// initializer. Tests that need to remove it reach for this instance via
    /// <see cref="DeviceEnrichers.Unregister(IDeviceEnricher)"/>.
    /// </summary>
    public static SensorEnricher Instance { get; } = new SensorEnricher();

    /// <summary>HID usage page for the Sensor page (HID Usage Tables §3).</summary>
    private const ushort HidSensorUsagePage = 0x20;

    private const string LinuxIioSubsystem = "iio";

    private static readonly Guid s_sensorClassGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Sensor);

    private static readonly ImmutableHashSet<string> s_emitsTags =
        ImmutableHashSet.Create(StringComparer.Ordinal, DeviceTags.Sensor);

    // Mirrors the sensor signal in each platform's category map: the Windows
    // Sensor setup class, the Linux iio subsystem, and macOS HID devices
    // (refined to usage page 0x20 by CanEnrich).
    private static readonly EnricherScope s_scope = new(
        WindowsClassGuids: [Periphery.Windows.DeviceClassGuids.Sensor],
        LinuxSubsystems: [LinuxIioSubsystem],
        MacOSClasses: ["IOHIDDevice"]);

    /// <inheritdoc/>
    public IReadOnlySet<string> EmitsTags => s_emitsTags;

    /// <inheritdoc/>
    public EnricherScope Scope => s_scope;

    /// <inheritdoc/>
    public bool CanEnrich(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.ClassGuid == s_sensorClassGuid                                  // Windows
            || string.Equals(device.Subsystem, LinuxIioSubsystem, StringComparison.Ordinal) // Linux
            || device.HidUsagePage == HidSensorUsagePage;                             // macOS
    }

    /// <inheritdoc/>
    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!CanEnrich(device) || device.Tags.Contains(DeviceTags.Sensor))
            return Task.FromResult(device);
        return Task.FromResult(device with { Tags = device.Tags.Add(DeviceTags.Sensor) });
    }
}

/// <summary>
/// Registers <see cref="SensorEnricher.Instance"/> with the core
/// <see cref="DeviceEnrichers"/> registry on first load of the Periphery
/// assembly, so the Sensor tag is emitted without any consumer-side wiring.
/// </summary>
internal static class SensorEnricherRegistration
{
    // CA2255: ModuleInitializer in library code is "advanced" — accepted, this
    // is exactly the auto-registration the ADR-0024 §3c hook exists to enable.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => DeviceEnrichers.Register(SensorEnricher.Instance);
}
