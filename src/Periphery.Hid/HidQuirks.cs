// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Periphery.Hid.Codecs;

namespace Periphery.Hid;

/// <summary>
/// Registry mapping vendor-defined HID devices to the codec that knows
/// their protocol. Centralises the "white-label HID quirk" problem (per
/// ADR-0048) so consumers don't need to special-case individual UPS clones
/// on Cypress 0665 / Voltronic silicon themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Baseline registrations</b> for known clones are populated by a
/// <see cref="ModuleInitializerAttribute"/> in this assembly (runs on
/// first access to any type from <c>Periphery.Hid</c>). Today's baseline:
/// </para>
/// <list type="bullet">
/// <item><c>0665:5161</c> — WayTech / generic Cypress 0665 clone → <see cref="MegatecQxCodec"/></item>
/// </list>
/// <para>
/// <b>Consumer-side overrides</b> via <see cref="RegisterUps"/> let an
/// application register a new clone (or override the baseline with a
/// better-matched codec) at startup without waiting for a Periphery
/// release. Last-write-wins; the <see cref="UpsCodecOverridden"/> event
/// fires when an existing entry is replaced so consumers can surface
/// the override in their own logs.
/// </para>
/// <para>
/// <b>Future generalisation</b> — when the second domain shows up
/// (vendor HID scanner-beep, cash-drawer-kick, etc.) the registration
/// surface generalises with parallel methods (<c>RegisterScannerBeep</c>,
/// <c>RegisterCashDrawer</c>, …). See ADR-0047 OQ-006 for the
/// single-class-vs-per-domain disposition.
/// </para>
/// </remarks>
public static class HidQuirks
{
    private static readonly ConcurrentDictionary<(HardwareId Vid, HardwareId Pid), IHidUpsCodec>
        _ups = new();

    /// <summary>
    /// Fires when <see cref="RegisterUps"/> replaces an existing codec
    /// registration for a (vid, pid) pair. Subscribe to surface
    /// unintentional overrides in your application's log — the static
    /// API doesn't take a logger dependency by design (Periphery.Hid
    /// has no logging framework opinion), so consumer-side observation
    /// is the right place to surface the event.
    /// </summary>
    public static event Action<HardwareId, HardwareId>? UpsCodecOverridden;

    /// <summary>
    /// Registers a vendor-defined HID UPS that doesn't implement the
    /// standard HID Power Device class. The enricher will dispatch to
    /// <paramref name="codec"/> when a device matching
    /// <paramref name="vendorId"/> and <paramref name="productId"/>
    /// is enriched.
    /// </summary>
    /// <remarks>
    /// <b>Last-write-wins.</b> If an entry already exists for the
    /// (vid, pid) pair (baseline or prior consumer registration), it's
    /// replaced silently and <see cref="UpsCodecOverridden"/> fires.
    /// For collision-aware registration use <see cref="TryRegisterUps"/>.
    /// </remarks>
    public static void RegisterUps(HardwareId vendorId, HardwareId productId, IHidUpsCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        var key = (vendorId, productId);
        bool isOverride = _ups.ContainsKey(key);
        _ups[key] = codec;
        if (isOverride)
            UpsCodecOverridden?.Invoke(vendorId, productId);
    }

    /// <summary>
    /// Collision-aware variant. Returns <c>true</c> on first registration
    /// and <c>false</c> when an entry for (vid, pid) already exists;
    /// <paramref name="wasOverride"/> indicates whether an existing
    /// entry *would* have been replaced (useful for diagnostic logging
    /// before the caller decides to force via <see cref="RegisterUps"/>).
    /// Does not modify the table on collision.
    /// </summary>
    public static bool TryRegisterUps(
        HardwareId vendorId, HardwareId productId, IHidUpsCodec codec,
        out bool wasOverride)
    {
        ArgumentNullException.ThrowIfNull(codec);

        wasOverride = _ups.ContainsKey((vendorId, productId));
        if (wasOverride)
            return false;

        return _ups.TryAdd((vendorId, productId), codec);
    }

    /// <summary>
    /// Returns the registered UPS codec for a device, or <c>null</c> if
    /// no codec is registered. Exposed so consumers can drive their own
    /// polling loops over the codec — continuous polling is intentionally
    /// NOT a Periphery concern (cadence is operational policy; per ADR-0048
    /// OQ-003).
    /// </summary>
    public static IHidUpsCodec? GetUpsCodec(HardwareId vendorId, HardwareId productId)
        => _ups.TryGetValue((vendorId, productId), out var codec) ? codec : null;

    /// <summary>
    /// Internal lookup used by <see cref="HidBatteryEnricher"/> (for
    /// classification) and <see cref="MegatecQxCodec"/> consumers (for
    /// snapshot reads via <see cref="HidBattery.ReadSnapshotAsync"/>).
    /// Returns <c>true</c> + the codec when registered, <c>false</c>
    /// otherwise.
    /// </summary>
    internal static bool TryGetUpsCodec(
        HardwareId vendorId, HardwareId productId, out IHidUpsCodec? codec)
        => _ups.TryGetValue((vendorId, productId), out codec);

    /// <summary>
    /// Reserved for test isolation — clears every consumer-registered
    /// entry AND re-runs the baseline registration so each test starts
    /// from a known state.
    /// </summary>
    internal static void ResetForTests()
    {
        _ups.Clear();
        BaselineRegistrations.RegisterAll();
    }
}

/// <summary>
/// Built-in <see cref="HidQuirks"/> registrations for known white-label
/// UPS clones. Runs on first access to any type from <c>Periphery.Hid</c>.
/// Grow as new hardware is confirmed against a codec.
/// </summary>
internal static class BaselineRegistrations
{
    // CA2255 warns that ModuleInitializer in library code is "advanced"
    // — we accept that explicitly: this is exactly the case the ADR
    // signed off on (extension-style auto-registration of vendor quirks
    // when Periphery.Hid is loaded). Suppressed locally so the rest of
    // the assembly still benefits from the rule.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterAll()
    {
        // WayTech / generic Cypress 0665 clone, seen on a deployed UPS.
        // The VID:PID only gets us to the Megatec-Qx codec; the actual
        // status dialect (Q1 vs QS vs …) is resolved by runtime probe
        // inside the codec, because the same 0665:5161 silicon ships
        // firmware for either. The unit tested answers QS, not Q1
        // (ADR-0048 addendum 2026-06-05).
        HidQuirks.RegisterUps(
            new HardwareId(0x0665),
            new HardwareId(0x5161),
            new MegatecQxCodec());

        // Add other known clones here as we get hardware to test
        // them against. Candidates from nutdrv_qx's table:
        //   06DA:* — Phoenixtec family (many sub-PIDs)
        //   0925:*  — Lakeview clones
        //   0F03:*  — generic Voltronic re-skins
        //   etc.
    }
}
