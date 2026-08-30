// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading;
using System.Threading.Tasks;

namespace Periphery;

/// <summary>
/// Cross-cutting enrichment pass that runs against every
/// <see cref="DeviceInfo"/> emitted by the platform provider, after
/// the OS-metadata enumeration produces a base record. Enrichers add
/// typed properties or capability tags by reading <em>only</em> from
/// OS metadata sources (registry, sysfs, IOKit property bags, internal
/// lookup tables) — never by opening a device handle.
/// </summary>
/// <remarks>
/// <para><b>Zero-I/O invariant (ADR-0026).</b>
/// <see cref="EnrichAsync"/> implementations must not open device
/// handles or perform device I/O. Handle-gated data uses the Option D
/// static snapshot helper pattern instead (e.g.
/// <c>UsbPort.ReadDescriptorsAsync</c>,
/// <c>HidBattery.ReadSnapshotAsync</c>), which keeps the I/O cost
/// explicit at the call site and preserves <see cref="DeviceInfo"/>'s
/// "zero-I/O snapshot" guarantee.</para>
/// <para><b>Registration.</b> Implementations register via
/// <see cref="DeviceEnrichers.Register(IDeviceEnricher)"/> — typically
/// from a <c>[ModuleInitializer]</c> in an extension package so the
/// enricher is available the first time core enumeration runs. The
/// provider pipeline iterates the registry per device, calling
/// <see cref="CanEnrich"/> first as a cheap discriminator before the
/// async <see cref="EnrichAsync"/>.</para>
/// <para><b>Async signature.</b> The interface is async even when most
/// concrete enrichers are sync (a dictionary lookup wrapping its result
/// in <see cref="Task.FromResult{TResult}"/>). The signature reserves
/// room for future enrichers that need WMI / slow-sysfs reads without
/// forcing a contract change. See ADR-0024 §3c for the original
/// specification.</para>
/// </remarks>
public interface IDeviceEnricher
{
    /// <summary>
    /// Cheap discriminator — returns <c>true</c> when this enricher
    /// might add anything to <paramref name="device"/>. Called by the
    /// pipeline to short-circuit the more expensive
    /// <see cref="EnrichAsync"/> path. Must be free of side-effects.
    /// </summary>
    bool CanEnrich(DeviceInfo device);

    /// <summary>
    /// Returns an enriched copy of <paramref name="device"/> (typically
    /// via a <c>with</c> expression) or the original instance when
    /// nothing applies. Must not open device handles or perform device
    /// I/O — reads OS metadata sources only (ADR-0026).
    /// </summary>
    Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct);
}
