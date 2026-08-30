// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Hid.Codecs;

namespace Periphery.Hid;

/// <summary>
/// Pure-metadata enricher that tags HID-class devices with
/// <see cref="DeviceTags.Battery"/> when an <see cref="IHidUpsCodec"/>
/// is registered in <see cref="HidQuirks"/> for the device's
/// (<see cref="DeviceInfo.VendorId"/>, <see cref="DeviceInfo.ProductId"/>).
/// No device I/O — the classification is a dictionary lookup against
/// the quirks table.
/// </summary>
/// <remarks>
/// <para><b>ADR-0026 compliant (sub-kind A).</b> Does not open a HID
/// handle, does not perform device I/O, does not populate any battery
/// field on <see cref="DeviceInfo"/>.
/// <see cref="DeviceInfo.BatteryChargePercent"/>,
/// <see cref="DeviceInfo.BatteryStatus"/>,
/// <see cref="DeviceInfo.IsExternalPowerConnected"/>, and
/// <see cref="DeviceInfo.IsBatteryLow"/> are intentionally left
/// untouched — they retain their single meaning (populated by the OS
/// at enumeration time; <c>null</c> otherwise).</para>
/// <para><b>Tag metadata (ADR-0051 §5).</b> Implements
/// <see cref="ITagEmittingEnricher"/>: <see cref="EmitsTags"/> declares
/// <see cref="DeviceTags.Battery"/> and <see cref="Scope"/> declares the HID
/// subsystem, so a bare <c>WithTag(DeviceTags.Battery)</c> query (no
/// <c>OfCategory</c>) can be scoped to HID-class devices once a provider
/// consults <see cref="DeviceEnrichers.ScopeForTags(IReadOnlySet{string})"/>.
/// System batteries surfaced under <see cref="DeviceCategory.Battery"/> are
/// tagged separately by core's <c>WindowsBatteryEnricher</c>; the full Battery
/// scope is the union of both, which the registry computes.</para>
/// <para><b>Auto-registration.</b> A singleton <see cref="Instance"/>
/// is registered with <see cref="DeviceEnrichers"/> via a module
/// initializer in this assembly. The first time any type from
/// <c>Periphery.Hid</c> loads, the registration runs and subsequent
/// core enumerations invoke <see cref="EnrichAsync"/> automatically —
/// no consumer-side <c>Select(Enrich)</c> dance required.</para>
/// <para>For live battery state on a HID UPS, consumers call
/// <see cref="HidBattery.ReadSnapshotAsync"/> explicitly — the
/// ADR-0026 Option D static snapshot helper that opens a transient
/// handle, runs the codec, and returns a <see cref="HidBatterySnapshot"/>.
/// I/O cost is visible at the call site.</para>
/// </remarks>
public sealed class HidBatteryEnricher : ITagEmittingEnricher
{
    /// <summary>
    /// Singleton instance registered with <see cref="DeviceEnrichers"/>
    /// by the assembly's module initializer. Tests that need to remove
    /// the enricher (or swap it for a fake) reach for this instance via
    /// <see cref="DeviceEnrichers.Unregister(IDeviceEnricher)"/>.
    /// </summary>
    public static HidBatteryEnricher Instance { get; } = new HidBatteryEnricher();

    private static readonly ImmutableHashSet<string> s_emitsTags =
        ImmutableHashSet.Create(StringComparer.Ordinal, DeviceTags.Battery);

    // A HID-class UPS enumerates under the HID subsystem on every platform.
    // Declaring that scope lets a bare WithTag(Battery) query reach it once
    // provider activation lands (ADR-0051 §5). Tokens mirror the HID arms of
    // the three platform category maps.
    private static readonly EnricherScope s_scope = new(
        WindowsClassGuids: [Periphery.Windows.DeviceClassGuids.HidClass],
        LinuxSubsystems: ["hid", "input"],
        MacOSClasses: ["IOHIDDevice"]);

    /// <inheritdoc/>
    public IReadOnlySet<string> EmitsTags => s_emitsTags;

    /// <inheritdoc/>
    public EnricherScope Scope => s_scope;

    /// <inheritdoc/>
    public bool CanEnrich(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        // Tag only the HID-bus interface, not the USB-bus parent that
        // shares the same VID/PID. Both enumerate as Category=Hid on
        // Windows for composite devices like the WayTech UPS, but only
        // the HID-bus child has a HID interface CreateFile can open.
        // Tagging the USB parent here would cause downstream snapshot
        // reads to fail with ERROR_FILE_NOT_FOUND.
        return device is { Category: DeviceCategory.Hid, BusType: BusType.HID };
    }

    /// <inheritdoc/>
    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!CanEnrich(device))
            return Task.FromResult(device);

        if (device.VendorId is null || device.ProductId is null)
            return Task.FromResult(device);

        if (!HidQuirks.TryGetUpsCodec(device.VendorId.Value, device.ProductId.Value, out _))
            return Task.FromResult(device);

        // Already tagged — don't add a duplicate (idempotent re-enrichment).
        if (device.Tags.Contains(DeviceTags.Battery))
            return Task.FromResult(device);

        return Task.FromResult(device with { Tags = device.Tags.Add(DeviceTags.Battery) });
    }
}

/// <summary>
/// Module initializer for <c>Periphery.Hid</c> — registers
/// <see cref="HidBatteryEnricher.Instance"/> with the core
/// <see cref="DeviceEnrichers"/> registry so consumers don't need to
/// invoke the enricher themselves. Runs on first access to any type
/// from this assembly.
/// </summary>
internal static class HidEnricherRegistration
{
    // CA2255 warns that ModuleInitializer in library code is "advanced"
    // — accepted explicitly: this is the case the ADR-0024 §3c hook
    // exists to enable (extension-style auto-registration when the
    // extension assembly loads).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        DeviceEnrichers.Register(HidBatteryEnricher.Instance);
    }
}
