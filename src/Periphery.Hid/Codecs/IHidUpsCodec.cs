// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Codecs;

/// <summary>
/// Snapshot-read interface for a HID-class UPS. Implementations encapsulate
/// a specific vendor protocol (Megatec Q1, Voltronic QS, MegaTec II, …) and
/// translate the protocol's response into a portable
/// <see cref="HidBatterySnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport-agnostic.</b> The codec takes an opened <see cref="HidDevice"/>
/// and chooses its own transport (feature reports vs input/output reports
/// with fragmentation). Standard HID Power Device Class (usage page 0x84 /
/// 0x85) implementations use feature reports; non-compliant vendor-defined
/// devices (Cypress 0665 family, others) typically use input/output reports
/// with response fragmentation across multiple reads. The
/// <see cref="HidQuirks"/> table maps (VendorId, ProductId) pairs to the
/// codec implementation that understands their wire format.
/// </para>
/// <para>
/// <b>Read-only by design (ADR-0048 OQ-001).</b> The codec exposes status
/// reads only. A future UPS *control* surface (graceful-shutdown signaling,
/// self-test triggers, beeper mute) will live behind a separate
/// <c>IHidUpsControl</c> interface. Codec implementations should be built
/// on top of a reusable wire helper (see <see cref="MegatecWire"/>) so
/// that future control implementations can share the wire format without
/// duplication.
/// </para>
/// <para>
/// <b>Coexistence.</b> The HID input stream is multicast — any vendor
/// monitoring software (ViewPower, NUT, etc.) that has the device open
/// will see commands and responses on its own handle. Codec implementations
/// must look for their own command's response by prefix and tolerate
/// noise from other consumers on the input stream.
/// </para>
/// </remarks>
public interface IHidUpsCodec
{
    /// <summary>
    /// Reads the current battery / line state from <paramref name="device"/>.
    /// </summary>
    /// <param name="device">An opened HID device handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current battery snapshot.</returns>
    /// <exception cref="HidTransferException">
    /// Thrown if the device doesn't respond within the codec's timeout,
    /// or the response can't be parsed.
    /// </exception>
    ValueTask<HidBatterySnapshot> ReadSnapshotAsync(HidDevice device, CancellationToken ct);
}
