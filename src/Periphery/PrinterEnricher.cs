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
/// Core enricher that tags printers and print queues with
/// <see cref="DeviceTags.Printer"/>. Replaces the former
/// <c>DeviceCategory.Printer</c> (ADR-0051): a printer is surfaced as one of the
/// Windows <c>Printer</c> / <c>PnpPrinters</c> / <c>PrintQueue</c> setup classes,
/// or a USB device whose class code is <c>0x07</c> (Printer).
/// </summary>
/// <remarks>
/// <para><b>ADR-0026 compliant.</b> Pure metadata over fields populated at
/// enumeration time — <see cref="DeviceInfo.ClassGuid"/> (Windows) and
/// <see cref="DeviceInfo.UsbClassCode"/>.</para>
/// <para><b>USB-class coverage.</b> The <c>0x07</c> check is effective wherever
/// <see cref="DeviceInfo.UsbClassCode"/> is populated — Windows today. Linux and
/// macOS light up once the cross-platform UsbClassCode population lands (ADR-0051
/// step 2). The Windows setup-class paths work now via
/// <see cref="DeviceInfo.ClassGuid"/>.</para>
/// </remarks>
public sealed class PrinterEnricher : ITagEmittingEnricher
{
    /// <summary>Singleton registered with <see cref="DeviceEnrichers"/> at module init.</summary>
    public static PrinterEnricher Instance { get; } = new PrinterEnricher();

    private const byte UsbPrinterClass = 0x07;

    private static readonly Guid[] s_printerClassGuids =
    [
        Guid.Parse(Periphery.Windows.DeviceClassGuids.Printer),
        Guid.Parse(Periphery.Windows.DeviceClassGuids.PnpPrinters),
        Guid.Parse(Periphery.Windows.DeviceClassGuids.PrintQueue),
    ];

    private static readonly ImmutableHashSet<string> s_emitsTags =
        ImmutableHashSet.Create(StringComparer.Ordinal, DeviceTags.Printer);

    private static readonly EnricherScope s_scope = new(
        WindowsClassGuids:
        [
            Periphery.Windows.DeviceClassGuids.Printer,
            Periphery.Windows.DeviceClassGuids.PnpPrinters,
            Periphery.Windows.DeviceClassGuids.PrintQueue,
        ],
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
        return (device.ClassGuid is { } g && Array.IndexOf(s_printerClassGuids, g) >= 0)  // Windows setup classes
            || device.UsbClassCode?.Class == UsbPrinterClass;                             // USB Printer (0x07), where reported
    }

    /// <inheritdoc/>
    public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!CanEnrich(device) || device.Tags.Contains(DeviceTags.Printer))
            return Task.FromResult(device);
        return Task.FromResult(device with { Tags = device.Tags.Add(DeviceTags.Printer) });
    }
}

/// <summary>Registers <see cref="PrinterEnricher.Instance"/> on first load of the Periphery assembly.</summary>
internal static class PrinterEnricherRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => DeviceEnrichers.Register(PrinterEnricher.Instance);
}
