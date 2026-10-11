// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Periphery.Usb.Browser.Core;

namespace Periphery.Usb.Browser;

/// <summary>
/// WebUSB in a browser: ask the user for a device, list the devices the page has been granted, and
/// open them with <see cref="UsbDevice.OpenAsync(DeviceInfo, CancellationToken)"/> (ADR-0092).
/// </summary>
/// <remarks>
/// <para>A page sees only the devices the user has granted it, one chooser pick at a time. There
/// is no way to enumerate the machine's devices, so <c>Devices.*</c> still throws
/// <see cref="PlatformNotSupportedException"/> in a browser.</para>
/// <para>A device with a serial number gets an id that survives a replug, a reload and a browser
/// restart: <c>webusb:VVVV:PPPP:&lt;serial&gt;</c>. One without, or two attached devices sharing
/// one, gets <c>webusb-session:VVVV:PPPP:&lt;n&gt;</c>, which lasts until it is unplugged.</para>
/// </remarks>
[SupportedOSPlatform("browser")]
public static class BrowserUsb
{
    private static Task? s_initialized;
    private static readonly WebUsbGrantSync s_grants = new(GetDevicesAsync);

    /// <summary>
    /// Loads the package's JS module and lets <see cref="UsbDevice.OpenAsync(DeviceInfo, CancellationToken)"/>
    /// open granted devices. Call it once at startup. Later calls return the first call's task.
    /// </summary>
    /// <remarks>
    /// Run it before any click that requests a device. The browser lets a page open its chooser
    /// only for a few seconds after a user gesture, and loading the module inside that window
    /// would spend it.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The browser has no WebUSB.</exception>
    public static Task InitializeAsync(CancellationToken ct = default)
    {
        // A failed or cancelled start leaves nothing installed, so the next call tries again.
        if (s_initialized is { IsFaulted: true } or { IsCanceled: true })
            s_initialized = null;
        return s_initialized ??= InitializeCoreAsync(ct);
    }

    private static async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Rooted at the document's base URI. A relative path would resolve against the runtime's
        // own script under _framework/ (ADR-0092 D10).
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document")
            ?? throw new PlatformNotSupportedException("BrowserUsb needs a page with a document; it does not run in a worker.");
        string baseUri = document.GetPropertyAsString("baseURI")
            ?? throw new PlatformNotSupportedException("The document has no base URI.");
        var moduleUrl = new Uri(new Uri(baseUri), WebUsbInterop.ModulePath);

        await JSHost.ImportAsync(WebUsbInterop.Module, moduleUrl.ToString(), ct).ConfigureAwait(false);

        if (!WebUsbInterop.IsSupported())
            throw new PlatformNotSupportedException(
                "This browser has no WebUSB (navigator.usb). WebUSB needs a Chromium-based browser and a page served over HTTPS or from localhost.");

        UsbBackendHost.Browser = OpenGrantedAsync;
    }

    /// <summary>
    /// Opens the browser's device chooser, showing the devices that match any of
    /// <paramref name="filters"/>, and returns the one the user picks.
    /// </summary>
    /// <returns>
    /// The picked device, or <see langword="null"/> when the user dismissed the chooser. A browser
    /// with no chooser to show also returns <see langword="null"/>; the two cannot be told apart.
    /// </returns>
    /// <remarks>
    /// Call it from a user gesture such as a click handler. The grant persists in the browser,
    /// for a device with a serial number, until the user revokes it, so a reloaded page finds it
    /// with <see cref="GetGrantedDevicesAsync"/> without asking again.
    /// </remarks>
    /// <exception cref="ArgumentException">A filter sets a field WebUSB requires another field for.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="InitializeAsync"/> has not completed, or the browser refused to show the chooser,
    /// usually because the call was not made from a user gesture.
    /// </exception>
    public static async Task<DeviceInfo?> RequestDeviceAsync(
        IReadOnlyList<UsbDeviceRequestFilter> filters, CancellationToken ct = default)
    {
        string json = WebUsbJson.WriteFilters(filters);
        EnsureInitialized();

        int op = WebUsbInterop.NextOperation();
        int handle = await WebUsbInterop.RequestDeviceAsync(op, json).ConfigureAwait(false);
        if (handle < 0)
        {
            var rejection = WebUsbJson.ReadRejection(WebUsbInterop.TakeError(op));
            if (rejection.Name == WebUsbOutcomes.NotFoundError)
                return null;

            var inner = new WebUsbRejectionException(rejection);
            throw new InvalidOperationException(
                rejection.Name == WebUsbOutcomes.SecurityError
                    ? "The browser refused to show the USB device chooser. Request a device only from a user gesture, such as a click handler."
                    : $"The browser refused to show the USB device chooser ({rejection.Name}: {rejection.Message}).",
                inner);
        }

        // The grant stands whatever the caller does now; a cancellation only stops the lookup.
        ct.ThrowIfCancellationRequested();
        var grants = await SyncAsync().ConfigureAwait(false);
        return grants.Grants.TryGetValue(new GrantHandle(handle), out var grant)
            ? UsbGrantProjection.ToDeviceInfo(grant)
            : null;
    }

    /// <summary>
    /// The devices the page has been granted that are attached now. Needs no user gesture, so a
    /// reloaded page uses it to find a device the user picked before.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="InitializeAsync"/> has not completed.</exception>
    /// <exception cref="UsbException">The browser refused to list the devices.</exception>
    public static async Task<IReadOnlyList<DeviceInfo>> GetGrantedDevicesAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        ct.ThrowIfCancellationRequested();

        var grants = await SyncAsync().ConfigureAwait(false);
        return grants.Grants.Values
            .OrderBy(g => g.Handle.Value)
            .Select(UsbGrantProjection.ToDeviceInfo)
            .ToArray();
    }

    private static void EnsureInitialized()
    {
        if (s_initialized is not { IsCompletedSuccessfully: true })
            throw new InvalidOperationException("BrowserUsb.InitializeAsync must complete before devices are requested or listed.");
    }

    /// <summary>
    /// Reconciles the document's registry with <c>getDevices()</c>, so ids are minted against the
    /// devices attached now. One reconciliation runs at a time (<see cref="WebUsbGrantSync"/>).
    /// </summary>
    private static Task<UsbGrantRegistry> SyncAsync() => s_grants.SyncAsync();

    private static async Task<IReadOnlyList<(GrantHandle Handle, UsbGrantFacts Facts)>> GetDevicesAsync()
    {
        int op = WebUsbInterop.NextOperation();
        string? json = await WebUsbInterop.GetDevicesAsync(op).ConfigureAwait(false);
        if (json is null)
        {
            var rejection = WebUsbJson.ReadRejection(WebUsbInterop.TakeError(op));
            throw new UsbException(
                $"The browser refused to list the granted USB devices ({rejection.Name}: {rejection.Message}).",
                new WebUsbRejectionException(rejection));
        }

        return WebUsbJson.ReadDevices(json);
    }

    private static async Task<IUsbBackend> OpenGrantedAsync(DeviceInfo device, CancellationToken ct)
    {
        var grants = await SyncAsync().ConfigureAwait(false);
        var grant = UsbGrantIds.Resolve(grants, device.Id)
            ?? throw new UsbDeviceNotFoundException(
                $"No attached device this page has been granted has the id '{device.Id}'. It may have been " +
                "unplugged, its grant revoked, or its id minted in another page.",
                new IOException("The id is not among the devices getDevices() returns."),
                device.Id);

        return await WebUsbBackend.OpenAsync(new JsWebUsbDevice(grant.Handle.Value), device.Id, ct).ConfigureAwait(false);
    }
}
