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
/// Core enricher that tags biometric readers (fingerprint, facial-recognition)
/// with <see cref="DeviceTags.Biometric"/>. Replaces the former
/// <c>DeviceCategory.Biometric</c> (ADR-0051).
/// </summary>
/// <remarks>
/// <para><b>ADR-0026 compliant.</b> Pure metadata over
/// <see cref="DeviceInfo.ClassGuid"/>, populated at enumeration time; opens no
/// handle and performs no device I/O.</para>
/// <para><b>Windows-only signal (by design).</b> Only Windows surfaces biometric
/// devices as a dedicated subsystem — the <c>Biometric</c> setup class. USB has
/// no biometric base class (readers are vendor-specific, <c>0xFF</c>), so there
/// is deliberately <i>no</i> USB-class branch — matching <c>0xFF</c> would tag
/// every vendor-specific device. The former category was likewise functional
/// only on Windows (Linux/macOS resolved a biometric USB device to <c>Usb</c>),
/// so this is a faithful, zero-regression demotion. A future platform that gains
/// a real biometric signal adds a <see cref="CanEnrich"/> branch and a
/// <see cref="Scope"/> arm; the shape is already in place.</para>
/// </remarks>
public sealed class BiometricEnricher : ITagEmittingEnricher
{
    /// <summary>Singleton registered with <see cref="DeviceEnrichers"/> at module init.</summary>
    public static BiometricEnricher Instance { get; } = new BiometricEnricher();

    private static readonly Guid s_biometricClassGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Biometric);

    private static readonly ImmutableHashSet<string> s_emitsTags =
        ImmutableHashSet.Create(StringComparer.Ordinal, DeviceTags.Biometric);

    private static readonly EnricherScope s_scope = new(
        WindowsClassGuids: [Periphery.Windows.DeviceClassGuids.Biometric],
        LinuxSubsystems: [],
        MacOSClasses: []);

    /// <inheritdoc/>
    public IReadOnlySet<string> EmitsTags => s_emitsTags;

    /// <inheritdoc/>
    public EnricherScope Scope => s_scope;

    /// <inheritdoc/>
    public bool CanEnrich(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.ClassGuid == s_biometricClassGuid;   // Windows (Biometric setup class)
    }

    /// <inheritdoc/>
    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!CanEnrich(device) || device.Tags.Contains(DeviceTags.Biometric))
            return Task.FromResult(device);
        return Task.FromResult(device with { Tags = device.Tags.Add(DeviceTags.Biometric) });
    }
}

/// <summary>Registers <see cref="BiometricEnricher.Instance"/> on first load of the Periphery assembly.</summary>
internal static class BiometricEnricherRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => DeviceEnrichers.Register(BiometricEnricher.Instance);
}
