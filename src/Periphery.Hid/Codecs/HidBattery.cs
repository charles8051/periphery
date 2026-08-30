// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Codecs;

/// <summary>
/// ADR-0026 Option D static snapshot helper for HID-class battery /
/// UPS devices. Opens a transient HID handle, looks up the registered
/// <see cref="IHidUpsCodec"/> for the device's (VendorId, ProductId)
/// via <see cref="HidQuirks"/>, runs the codec's snapshot read, and
/// closes the handle before returning.
/// </summary>
/// <remarks>
/// <para>
/// <b>The I/O cost is explicit at the call site.</b> Unlike a sub-kind-B
/// enricher that would silently open N handles during a bulk
/// enumeration, callers of this method know they're paying I/O cost
/// for exactly the devices they pass in. This matches the
/// <c>UsbPort.ReadDescriptorsAsync</c> pattern that ADR-0026 §Option D
/// establishes as the right shape for handle-gated metadata.
/// </para>
/// <para>
/// <b>Does not modify <see cref="DeviceInfo"/>.</b> Returns a
/// <see cref="HidBatterySnapshot"/> domain record — keeps
/// <see cref="DeviceInfo"/> as a pure zero-I/O enumeration snapshot
/// (the load-bearing invariant of ADR-0026).
/// </para>
/// <para>
/// <b>Polling cadence is consumer policy.</b> Applications that want
/// continuously-fresh battery state poll this method themselves at
/// whatever cadence their operational story demands — Periphery has
/// no opinion on cadence (per ADR-0048 OQ-003).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static class HidBattery
{
    /// <summary>
    /// Reads a battery snapshot from <paramref name="device"/> if a
    /// codec is registered in <see cref="HidQuirks"/> for its
    /// (VendorId, ProductId). Returns <c>null</c> when no codec
    /// matches (the device isn't a recognised UPS clone) — callers
    /// can treat that as "not a battery-readable device" without
    /// catching an exception.
    /// </summary>
    /// <exception cref="HidException">
    /// Thrown when the HID handle open fails (device locked
    /// exclusively, unplugged) or the codec read fails (timeout,
    /// malformed response).
    /// </exception>
    public static async Task<HidBatterySnapshot?> ReadSnapshotAsync(
        DeviceInfo device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.VendorId is null || device.ProductId is null)
            return null;

        if (!HidQuirks.TryGetUpsCodec(device.VendorId.Value, device.ProductId.Value, out var codec)
            || codec is null)
            return null;

        await using var hid = await HidDevice.OpenAsync(device, ct).ConfigureAwait(false);
        return await codec.ReadSnapshotAsync(hid, ct).ConfigureAwait(false);
    }
}
