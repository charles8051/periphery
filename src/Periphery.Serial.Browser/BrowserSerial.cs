// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Periphery.Serial.Browser.Core;

namespace Periphery.Serial.Browser;

/// <summary>
/// Web Serial in a browser: ask the user for a serial port and get it as a
/// <see cref="BrowserSerialPort"/> (ADR-0092 D4).
/// </summary>
/// <remarks>
/// A page sees only the ports the user has granted it, one chooser pick at a time. A port has no
/// id that survives a replug, so the page holds the port object it was given.
/// </remarks>
[SupportedOSPlatform("browser")]
public static class BrowserSerial
{
    private static Task? s_initialized;

    /// <summary>Loads the package's JS module. Call it once at startup; later calls return the first call's task.</summary>
    /// <remarks>
    /// Run it before any click that requests a port. The browser lets a page open its chooser only
    /// for a few seconds after a user gesture, and loading the module inside that window would
    /// spend it.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The browser has no Web Serial.</exception>
    public static Task InitializeAsync(CancellationToken ct = default)
    {
        // A failed or cancelled start leaves nothing loaded, so the next call tries again.
        if (s_initialized is { IsFaulted: true } or { IsCanceled: true })
            s_initialized = null;
        return s_initialized ??= InitializeCoreAsync(ct);
    }

    private static async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Rooted at the document's base URI; a relative path would resolve under _framework/
        // (ADR-0092 D10).
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document")
            ?? throw new PlatformNotSupportedException("BrowserSerial needs a page with a document; it does not run in a worker.");
        string baseUri = document.GetPropertyAsString("baseURI")
            ?? throw new PlatformNotSupportedException("The document has no base URI.");
        var moduleUrl = new Uri(new Uri(baseUri), WebSerialInterop.ModulePath);

        await JSHost.ImportAsync(WebSerialInterop.Module, moduleUrl.ToString(), ct).ConfigureAwait(false);

        if (!WebSerialInterop.IsSupported())
            throw new PlatformNotSupportedException(
                "This browser has no Web Serial (navigator.serial). Web Serial needs a Chromium-based browser and a page served over HTTPS or from localhost.");
    }

    /// <summary>
    /// Opens the browser's port chooser, showing the ports that match any of
    /// <paramref name="filters"/>, and returns the one the user picks, not yet open.
    /// </summary>
    /// <returns>
    /// The picked port, or <see langword="null"/> when the user dismissed the chooser. A browser with
    /// no chooser to show also returns <see langword="null"/>; the two cannot be told apart.
    /// </returns>
    /// <remarks>Call it from a user gesture such as a click handler.</remarks>
    /// <exception cref="ArgumentException">A filter sets <c>UsbProductId</c> without <c>UsbVendorId</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="InitializeAsync"/> has not completed, or the browser refused to show the chooser,
    /// usually because the call was not made from a user gesture.
    /// </exception>
    public static async Task<BrowserSerialPort?> RequestPortAsync(
        IReadOnlyList<SerialPortRequestFilter> filters, CancellationToken ct = default)
    {
        string json = WebSerialJson.WriteFilters(filters);
        if (s_initialized is not { IsCompletedSuccessfully: true })
            throw new InvalidOperationException("BrowserSerial.InitializeAsync must complete before a port is requested.");

        int op = WebSerialInterop.NextOperation();
        string? port = await WebSerialInterop.RequestPortAsync(op, json).ConfigureAwait(false);
        if (port is null)
        {
            var rejection = WebSerialJson.ReadRejection(WebSerialInterop.TakeError(op));
            if (rejection.Name == "NotFoundError")
                return null;

            var inner = new WebSerialRejectionException(rejection);
            throw new InvalidOperationException(
                rejection.Name == "SecurityError"
                    ? "The browser refused to show the serial port chooser. Request a port only from a user gesture, such as a click handler."
                    : $"The browser refused to show the serial port chooser ({rejection.Name}: {rejection.Message}).",
                inner);
        }

        // The grant stands whatever the caller does now.
        ct.ThrowIfCancellationRequested();
        var (handle, vendor, product) = WebSerialJson.ReadPort(port);
        return new BrowserSerialPort(new JsWebSerialPort(handle), vendor, product);
    }
}
