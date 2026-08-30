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
/// Core enricher that tags imaging devices (scanners, still-image / PTP cameras)
/// with <see cref="DeviceTags.Imaging"/>. Replaces the former
/// <c>DeviceCategory.Imaging</c> (ADR-0051): an imaging device is surfaced as the
/// Windows <c>Image</c> setup class or a USB device whose class code is
/// <c>0x06</c> (Still Image / PTP).
/// </summary>
/// <remarks>
/// <para><b>ADR-0026 compliant.</b> Pure metadata over fields populated at
/// enumeration time — <see cref="DeviceInfo.ClassGuid"/> (Windows) and
/// <see cref="DeviceInfo.UsbClassCode"/>.</para>
/// <para><b>USB-class coverage.</b> The <c>0x06</c> check is effective wherever
/// <see cref="DeviceInfo.UsbClassCode"/> is populated — Windows today. Linux and
/// macOS light up once the cross-platform UsbClassCode population lands (shared
/// with the SmartCard/Printer demotions, ADR-0051 step 2). The Windows
/// <c>Image</c> setup-class path works now via <see cref="DeviceInfo.ClassGuid"/>.</para>
/// </remarks>
public sealed class ImagingEnricher : ITagEmittingEnricher
{
    /// <summary>Singleton registered with <see cref="DeviceEnrichers"/> at module init.</summary>
    public static ImagingEnricher Instance { get; } = new ImagingEnricher();

    private const byte UsbImageClass = 0x06;

    private static readonly Guid s_imageClassGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Image);

    private static readonly ImmutableHashSet<string> s_emitsTags =
        ImmutableHashSet.Create(StringComparer.Ordinal, DeviceTags.Imaging);

    private static readonly EnricherScope s_scope = new(
        WindowsClassGuids: [Periphery.Windows.DeviceClassGuids.Image],
        LinuxSubsystems: ["usb"],
        MacOSClasses: ["IOUSBDevice", "IOUSBHostDevice"]);

    /// <inheritdoc/>
    public IReadOnlySet<string> EmitsTags => s_emitsTags;

    /// <inheritdoc/>
    public EnricherScope Scope => s_scope;

    /// <inheritdoc/>
    public bool CanEnrich(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.ClassGuid == s_imageClassGuid          // Windows (Image setup class)
            || device.UsbClassCode?.Class == UsbImageClass;  // USB Still-Image/PTP (0x06), where reported
    }

    /// <inheritdoc/>
    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!CanEnrich(device) || device.Tags.Contains(DeviceTags.Imaging))
            return Task.FromResult(device);
        return Task.FromResult(device with { Tags = device.Tags.Add(DeviceTags.Imaging) });
    }
}

/// <summary>Registers <see cref="ImagingEnricher.Instance"/> on first load of the Periphery assembly.</summary>
internal static class ImagingEnricherRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => DeviceEnrichers.Register(ImagingEnricher.Instance);
}
