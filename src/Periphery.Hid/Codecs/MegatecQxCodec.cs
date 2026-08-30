// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Codecs;

/// <summary>
/// <see cref="IHidUpsCodec"/> for the Megatec Qx family of HID UPSs — the wide
/// Cypress 0665 / Voltronic / Phoenixtec lineage behind most no-name clones
/// (WayTech, PowerWalker, Mustek, …). These devices share one status-response
/// <em>shape</em> but differ in the status <em>verb</em> they answer (Megatec
/// <c>Q1</c>, Voltronic <c>QS</c>, …), and VID:PID cannot tell them apart — the
/// very same <c>0665:5161</c> silicon ships firmware for both. This codec
/// resolves the dialect by runtime probe (claim-and-bind), modelled on NUT's
/// <c>nutdrv_qx</c> subdrivers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Functional core / imperative shell.</b> The parse is a pure total function
/// (<see cref="MegatecStatus.Parse"/>); the dialect set is data
/// (<see cref="MegatecDialect.Candidates"/>); this codec is the thin shell that
/// owns the I/O and the one-time handshake.
/// </para>
/// <para>
/// <b>Claim-and-bind.</b> On first contact with a device the codec probes each
/// candidate verb in turn (via <see cref="MegatecWire"/>) and binds the first
/// that returns a well-formed status line. The binding is cached per device id
/// for the process lifetime; subsequent reads send <em>only</em> the bound verb
/// — a one-time handshake, not a per-read fallback. The codec instance is
/// long-lived (held in <see cref="HidQuirks"/>) while
/// <see cref="HidBattery.ReadSnapshotAsync"/> opens a transient handle per poll,
/// so it is the cache — not the handle — that makes detection a one-time cost.
/// </para>
/// <para>
/// <b>Self-healing.</b> If a bound verb ever stops returning a well-formed line
/// (a mis-detection from input cross-talk, or a different unit hot-swapped onto
/// the same port), the binding is dropped so the next read re-detects. A healthy
/// device never re-probes.
/// </para>
/// <para>
/// <b>Bus quietness during detection.</b> The HID input endpoint is multicast,
/// so a <em>different</em> consumer's status reply that shares our <c>'('</c>
/// prefix is indistinguishable from an answer to our own probe (the May-2026
/// "Q1 validated" reading in ADR-0048 was very likely exactly that — ViewPower's
/// <c>QS</c> reply misattributed to a <c>Q1</c> write). Detection is therefore
/// most reliable when this process is the sole consumer; the bound steady state
/// is unaffected.
/// </para>
/// </remarks>
public sealed class MegatecQxCodec : IHidUpsCodec
{
    /// <summary>
    /// Wall-clock cap on a single status round-trip (per dialect probe during
    /// detection, and per read once bound). Vendor monitors poll every ~2s and
    /// device-side latency is typically &lt;200ms; 3s gives a sleepy firmware
    /// headroom without stalling a polling loop noticeably.
    /// </summary>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Bound dialect per device id (<see cref="DeviceInfo.Id"/> — stable for a
    /// device on a port; a new id simply re-detects once, which is harmless).
    /// </summary>
    private readonly ConcurrentDictionary<string, MegatecDialect> _bound =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public async ValueTask<HidBatterySnapshot> ReadSnapshotAsync(
        HidDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        string id = device.DeviceInfo.Id;

        // ── Bound steady state: send only the verb we already negotiated. ──
        if (_bound.TryGetValue(id, out var bound))
        {
            var response = await MegatecWire.RequestAsync(
                device, bound.Verb, bound.ResponsePrefix, StatusTimeout, ct)
                .ConfigureAwait(false);

            if (MegatecStatus.IsWellFormed(response))
                return MegatecStatus.Parse(response!);

            // Bound verb went quiet — self-heal: drop the binding so the next
            // poll re-detects, then surface this miss to the caller.
            _bound.TryRemove(new KeyValuePair<string, MegatecDialect>(id, bound));
            throw new HidTransferException(
                $"Megatec status request to '{id}' using the bound '{bound.Verb}' verb did not " +
                $"return a well-formed response within {StatusTimeout.TotalSeconds:0}s. The dialect " +
                "binding has been cleared; the next read will re-detect.",
                new IOException("bound-verb status timeout"),
                id);
        }

        // ── First contact: claim-and-bind. ──
        var detected = await DetectAsync(
            (dialect, token) => MegatecWire.RequestAsync(
                device, dialect.Verb, dialect.ResponsePrefix, StatusTimeout, token),
            ct).ConfigureAwait(false);

        if (detected is null)
            throw new HidTransferException(
                $"No Megatec status dialect answered for '{id}'. Tried " +
                $"{string.Join(", ", MegatecDialect.Candidates.Select(c => c.Verb))} " +
                $"(up to {StatusTimeout.TotalSeconds:0}s each); none returned a well-formed status " +
                "line. The device may not be a Megatec-family UPS, may be busy with another " +
                "consumer, or may have been disconnected.",
                new IOException("dialect detection failed"),
                id);

        var (winner, winningResponse) = detected.Value;
        _bound[id] = winner;
        return MegatecStatus.Parse(winningResponse);
    }

    /// <summary>
    /// Claim-and-bind policy, separated from device I/O so it is unit-testable
    /// with a fake <paramref name="probe"/>. Probes
    /// <see cref="MegatecDialect.Candidates"/> in order and returns the first
    /// dialect whose probe yields a well-formed status line (paired with that
    /// line), or <c>null</c> if none do.
    /// </summary>
    internal static async ValueTask<(MegatecDialect Dialect, string Response)?> DetectAsync(
        Func<MegatecDialect, CancellationToken, ValueTask<string?>> probe,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);

        foreach (var dialect in MegatecDialect.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var response = await probe(dialect, ct).ConfigureAwait(false);
            if (MegatecStatus.IsWellFormed(response))
                return (dialect, response!);
        }

        return null;
    }
}
