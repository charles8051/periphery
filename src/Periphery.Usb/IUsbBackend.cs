// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Usb;

/// <summary>Platform abstraction for the raw-USB claim + transfer surface.</summary>
/// <remarks>
/// Mirrors the <c>IHidBackend</c> shape: descriptor metadata read at open, an
/// interface-claim pair, and the three transfer primitives. The public
/// <see cref="UsbDevice"/> wraps an instance of this and forwards to it after a
/// disposed-check. The Windows implementation is
/// <c>Periphery.Usb.Windows.WinUsbBackend</c>.
/// </remarks>
internal interface IUsbBackend : IAsyncDisposable
{
    // ── Descriptors (read at open) ─────────────────────────────────────
    UsbDeviceDescriptor DeviceDescriptor { get; }
    UsbConfigurationDescriptor Configuration { get; }

    // ── Interface claim ────────────────────────────────────────────────
    void ClaimInterface(byte interfaceNumber);
    void ReleaseInterface(byte interfaceNumber);

    // ── Transfers ──────────────────────────────────────────────────────
    Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct);
    Task<int> BulkReadAsync(byte endpointAddress, Memory<byte> buffer, CancellationToken ct);
    Task<int> BulkWriteAsync(byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct);
}
