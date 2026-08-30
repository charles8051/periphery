// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery;

/// <summary>
/// Runs the cross-cutting <see cref="DeviceEnrichers"/> registry pass over a
/// single <see cref="DeviceInfo"/>. Every platform provider invokes this after
/// building a device's base OS-metadata record (and, on Windows, after its
/// inline network / battery / DisplayConfig enrichers) so registered
/// extension-package enrichers see the fully OS-populated record and can add
/// capability tags (ADR-0047) or typed properties.
/// </summary>
/// <remarks>
/// <para><b>Cross-platform (ADR-0051 §5).</b> Promoted from the former
/// Windows-only <c>WindowsEnrichmentPipeline</c> so registered enrichers — and
/// therefore capability tags — fire on Linux and macOS too, not just Windows.
/// The Linux and macOS providers call the sync path from the tail of their
/// <c>ToDeviceInfo</c> builder (the single point every enumerate and monitor
/// path funnels through); the Windows provider keeps an explicit call after its
/// inline-enricher stage, which must run between <c>ToDeviceInfo</c> and this
/// registry pass.</para>
/// <para>Both an async path (Windows <c>EnumerateAsync</c>'s async iterator)
/// and a sync path (the other providers and the monitor
/// <c>[UnmanagedCallersOnly]</c>-rooted code paths) are exposed. The sync path
/// uses <c>GetAwaiter().GetResult()</c> — safe for the current crop of
/// enrichers, which are dictionary-lookup-sync and return completed tasks via
/// <see cref="Task.FromResult{TResult}(TResult)"/>. A future genuinely-async
/// enricher would block the calling thread in the sync path; document that
/// constraint on the enricher when it appears.</para>
/// <para>Per-enricher exceptions are caught and logged here so a misbehaving
/// extension can't nuke an entire enumeration — the device passes through
/// unenriched for that step and the next enricher runs.
/// <see cref="OperationCanceledException"/> always propagates.</para>
/// </remarks>
internal static class EnrichmentPipeline
{
    internal static async Task<DeviceInfo> RunRegisteredAsync(
        DeviceInfo device, CancellationToken ct, ILogger? logger = null)
    {
        var enrichers = DeviceEnrichers.Snapshot();
        for (int i = 0; i < enrichers.Length; i++)
        {
            var enricher = enrichers[i];
            if (!enricher.CanEnrich(device)) continue;
            try
            {
                device = await enricher.EnrichAsync(device, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Enricher {Enricher} threw on device {DeviceId}; continuing with unenriched device",
                    enricher.GetType().Name, device.Id);
            }
        }
        return device;
    }

    internal static DeviceInfo RunRegisteredSync(
        DeviceInfo device, CancellationToken ct, ILogger? logger = null)
    {
        var enrichers = DeviceEnrichers.Snapshot();
        for (int i = 0; i < enrichers.Length; i++)
        {
            var enricher = enrichers[i];
            if (!enricher.CanEnrich(device)) continue;
            try
            {
                var task = enricher.EnrichAsync(device, ct);
                device = task.IsCompletedSuccessfully ? task.Result : task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Enricher {Enricher} threw on device {DeviceId}; continuing with unenriched device",
                    enricher.GetType().Name, device.Id);
            }
        }
        return device;
    }
}
