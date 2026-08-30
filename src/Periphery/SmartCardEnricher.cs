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
/// Core enricher that tags smart-card readers with <see cref="DeviceTags.SmartCard"/>.
/// Replaces the former <c>DeviceCategory.SmartCard</c> (ADR-0051): a smart-card
/// reader is surfaced as the Windows <c>SmartCardReader</c> setup class, the
/// macOS <c>IOUSBSmartCardController</c> IOKit class, or a USB device whose
/// class code is <c>0x0B</c> (CCID).
/// </summary>
/// <remarks>
/// <para><b>ADR-0026 compliant.</b> Pure metadata over fields populated at
/// enumeration time — <see cref="DeviceInfo.ClassGuid"/> (Windows),
/// <see cref="DeviceInfo.IOServiceClass"/> (macOS), and
/// <see cref="DeviceInfo.UsbClassCode"/>.</para>
/// <para><b>USB-class coverage.</b> The <c>0x0B</c> check is effective wherever
/// <see cref="DeviceInfo.UsbClassCode"/> is populated — Windows today. Linux,
/// and the macOS USB Tier-2 fallback, light up once the cross-platform
/// UsbClassCode population lands (shared with the Imaging/Printer demotions,
/// ADR-0051 step 2). The macOS *primary* path (<c>IOUSBSmartCardController</c>)
/// works now via <see cref="DeviceInfo.IOServiceClass"/>.</para>
/// </remarks>
public sealed class SmartCardEnricher : ITagEmittingEnricher
{
    /// <summary>Singleton registered with <see cref="DeviceEnrichers"/> at module init.</summary>
    public static SmartCardEnricher Instance { get; } = new SmartCardEnricher();

    private const byte UsbSmartCardClass = 0x0B;
    private const string MacSmartCardClass = "IOUSBSmartCardController";

    private static readonly Guid s_smartCardReaderGuid =
        Guid.Parse(Periphery.Windows.DeviceClassGuids.SmartCardReader);

    private static readonly ImmutableHashSet<string> s_emitsTags =
        ImmutableHashSet.Create(StringComparer.Ordinal, DeviceTags.SmartCard);

    private static readonly EnricherScope s_scope = new(
        WindowsClassGuids: [Periphery.Windows.DeviceClassGuids.SmartCardReader],
        LinuxSubsystems: ["usb"],
        MacOSClasses: [MacSmartCardClass, "IOUSBDevice", "IOUSBHostDevice"]);

    /// <inheritdoc/>
    public IReadOnlySet<string> EmitsTags => s_emitsTags;

    /// <inheritdoc/>
    public EnricherScope Scope => s_scope;

    /// <inheritdoc/>
    public bool CanEnrich(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.ClassGuid == s_smartCardReaderGuid                                       // Windows
            || string.Equals(device.IOServiceClass, MacSmartCardClass, StringComparison.Ordinal) // macOS (direct class)
            || device.UsbClassCode?.Class == UsbSmartCardClass;                                // USB CCID (0x0B), where reported
    }

    /// <inheritdoc/>
    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!CanEnrich(device) || device.Tags.Contains(DeviceTags.SmartCard))
            return Task.FromResult(device);
        return Task.FromResult(device with { Tags = device.Tags.Add(DeviceTags.SmartCard) });
    }
}

/// <summary>Registers <see cref="SmartCardEnricher.Instance"/> on first load of the Periphery assembly.</summary>
internal static class SmartCardEnricherRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => DeviceEnrichers.Register(SmartCardEnricher.Instance);
}
