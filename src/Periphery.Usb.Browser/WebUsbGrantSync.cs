// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Usb.Browser.Core;

namespace Periphery.Usb.Browser;

/// <summary>
/// The document's grant registry, reconciled with <c>getDevices()</c> one call at a time.
/// </summary>
/// <remarks>
/// Two reconciliations in flight would apply in the order their <c>getDevices()</c> calls finish,
/// not the order their snapshots were taken. An older snapshot applied last could release a grant
/// the newer one admitted, or admit a replugged device's stale handle beside its new one and
/// demote the new one's durable id. Holding the gate across the call and the reconcile makes every
/// snapshot newer than the one applied before it.
/// </remarks>
internal sealed class WebUsbGrantSync(Func<Task<IReadOnlyList<(GrantHandle Handle, UsbGrantFacts Facts)>>> getDevices)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The registry as of the last completed reconciliation.</summary>
    public UsbGrantRegistry Current { get; private set; } = UsbGrantRegistry.Empty;

    /// <summary>Takes a fresh snapshot of the attached grants and reconciles the registry with it.</summary>
    public async Task<UsbGrantRegistry> SyncAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var attached = await getDevices().ConfigureAwait(false);
            Current = UsbGrantIds.Reconcile(Current, attached);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }
}
