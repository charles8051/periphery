// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor;

/// <summary>
/// A one-shot monitor-control handle (ADR-0058). Composes up to two
/// <b>independent</b> control planes over the same enumeration identity:
/// the DDC/CI VCP plane (brightness, contrast, input source, power — state
/// that lives in the panel) and the display-mode plane (resolution,
/// orientation, refresh — state that lives in the OS display stack).
/// </summary>
/// <remarks>
/// <para>
/// Check <see cref="SupportsVcp"/> / <see cref="SupportsDisplayMode"/> before
/// using a plane: a virtual display typically mode-sets but has no DDC
/// channel, a physical panel may offer both, and Linux currently has no
/// display-mode backend (ADR-0058 D9). Calls into an absent plane throw
/// <see cref="MonitorCapabilityException"/>.
/// </para>
/// <para>
/// This is the Layer 1 primitive: no reconnect behaviour — once the monitor
/// is unplugged the handle is dead. For a reconnect-resilient handle use
/// <see cref="MonitorDeviceProxy"/>.
/// </para>
/// </remarks>
public sealed class MonitorDevice : IAsyncDisposable
{
    private readonly IMonitorBackend? _vcp;
    private readonly IDisplayModeBackend? _displayMode;
    private MccsCapabilities? _cachedCapabilities;
    private bool _disposed;

    private MonitorDevice(DeviceInfo deviceInfo, IMonitorBackend? vcp, IDisplayModeBackend? displayMode)
    {
        DeviceInfo = deviceInfo;
        _vcp = vcp;
        _displayMode = displayMode;
    }

    /// <summary>The enumeration snapshot this handle was opened from.</summary>
    public DeviceInfo DeviceInfo { get; }

    /// <summary>True when this handle carries a live DDC/CI channel.</summary>
    public bool SupportsVcp => _vcp is not null;

    /// <summary>True when this handle can read and set display modes.</summary>
    public bool SupportsDisplayMode => _displayMode is not null;

    // -----------------------------------------------------------------------
    // Open
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens monitor-control planes for the device described by
    /// <paramref name="deviceInfo"/>. Succeeds when at least one plane
    /// resolves; throws <see cref="MonitorCapabilityException"/> when the
    /// monitor exposes no controllable plane at all.
    /// </summary>
    public static Task<MonitorDevice> OpenAsync(DeviceInfo deviceInfo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInfo.Id);
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
            return OpenWindowsAsync(deviceInfo, ct);

        if (OperatingSystem.IsLinux())
            return OpenLinuxAsync(deviceInfo, ct);

        throw new PlatformNotSupportedException(
            $"MonitorDevice.OpenAsync is not yet implemented on {Environment.OSVersion.Platform}. "
            + "The macOS backend is planned (ADR-0058).");
    }

    [SupportedOSPlatform("windows")]
    private static Task<MonitorDevice> OpenWindowsAsync(DeviceInfo deviceInfo, CancellationToken ct)
    {
        // Resolution + the DDC probe are synchronous OS calls; hop to the pool.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var resolved = Windows.MonitorPathResolver.Resolve(deviceInfo.Id);

            // The mode plane exists for every active path; the VCP plane only
            // where the DDC probe succeeds (TryOpen returns null otherwise).
            var displayMode = new Windows.GdiDisplayModeBackend(resolved.SourceGdiName, deviceInfo.Id);
            var vcp = Windows.DxvaMonitorBackend.TryOpen(resolved.HMonitor, deviceInfo.Id);

            return new MonitorDevice(deviceInfo, vcp, displayMode);
        }, ct);
    }

    [SupportedOSPlatform("linux")]
    private static Task<MonitorDevice> OpenLinuxAsync(DeviceInfo deviceInfo, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var vcp = Linux.I2cDdcMonitorBackend.TryOpen(deviceInfo.Id);
            if (vcp is null)
            {
                throw new MonitorCapabilityException(
                    $"Monitor '{deviceInfo.Id}' exposes no controllable plane: the DRM "
                    + "connector has no DDC link, and display-mode control is not yet "
                    + "available on Linux (ADR-0058 D9).", deviceInfo.Id);
            }

            return new MonitorDevice(deviceInfo, vcp, displayMode: null);
        }, ct);
    }

    // -----------------------------------------------------------------------
    // VCP plane — raw surface
    // -----------------------------------------------------------------------

    /// <summary>Reads any VCP feature — the raw escape hatch.</summary>
    public Task<VcpFeatureValue> GetVcpFeatureAsync(byte code, CancellationToken ct = default)
        => RequireVcp().GetVcpFeatureAsync(code, ct);

    /// <summary>Writes any VCP feature — the raw escape hatch.</summary>
    public Task SetVcpFeatureAsync(byte code, ushort value, CancellationToken ct = default)
        => RequireVcp().SetVcpFeatureAsync(code, value, ct);

    /// <summary>
    /// Fetches and parses the monitor's MCCS capabilities. The exchange takes
    /// tens of milliseconds per fragment, so the parsed result is cached for
    /// the handle's lifetime (capabilities are firmware-static).
    /// </summary>
    public async Task<MccsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        if (_cachedCapabilities is { } cached)
            return cached;

        string raw = await RequireVcp().GetCapabilitiesStringAsync(ct).ConfigureAwait(false);
        return _cachedCapabilities = MccsCapabilities.Parse(raw);
    }

    // -----------------------------------------------------------------------
    // VCP plane — semantic helpers (shared across platforms by construction)
    // -----------------------------------------------------------------------

    /// <summary>Reads brightness as a 0–1 fraction of the panel's reported maximum.</summary>
    public async Task<double> GetBrightnessAsync(CancellationToken ct = default)
    {
        var value = await GetVcpFeatureAsync(VcpCode.Luminance, ct).ConfigureAwait(false);
        return value.Maximum == 0 ? 0 : (double)value.Current / value.Maximum;
    }

    /// <summary>
    /// Sets brightness as a 0–1 fraction, normalized over the panel's
    /// reported maximum (panels disagree on the absolute scale; the fraction
    /// is the honest cross-panel unit).
    /// </summary>
    public async Task SetBrightnessAsync(double fraction, CancellationToken ct = default)
    {
        if (double.IsNaN(fraction))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        fraction = Math.Clamp(fraction, 0d, 1d);

        var value = await GetVcpFeatureAsync(VcpCode.Luminance, ct).ConfigureAwait(false);
        ushort target = (ushort)Math.Round(fraction * value.Maximum);
        await SetVcpFeatureAsync(VcpCode.Luminance, target, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the panel's power mode (VCP 0xD6).</summary>
    public async Task<MonitorPowerMode> GetPowerModeAsync(CancellationToken ct = default)
    {
        var value = await GetVcpFeatureAsync(VcpCode.PowerMode, ct).ConfigureAwait(false);
        return (MonitorPowerMode)value.Current;
    }

    /// <summary>Sets the panel's power mode (VCP 0xD6).</summary>
    public Task SetPowerModeAsync(MonitorPowerMode mode, CancellationToken ct = default)
        => SetVcpFeatureAsync(VcpCode.PowerMode, (ushort)mode, ct);

    /// <summary>Reads the active input source (VCP 0x60) as a raw MCCS value.</summary>
    public async Task<ushort> GetInputSourceAsync(CancellationToken ct = default)
    {
        var value = await GetVcpFeatureAsync(VcpCode.InputSource, ct).ConfigureAwait(false);
        return value.Current;
    }

    /// <summary>Switches the active input source (VCP 0x60).</summary>
    public Task SetInputSourceAsync(MonitorInputSource source, CancellationToken ct = default)
        => SetVcpFeatureAsync(VcpCode.InputSource, (ushort)source, ct);

    // -----------------------------------------------------------------------
    // Display-mode plane
    // -----------------------------------------------------------------------

    /// <summary>The mode currently driving the panel (live read, not the enumeration snapshot).</summary>
    public Task<DisplayMode> GetCurrentModeAsync(CancellationToken ct = default)
        => RequireDisplayMode().GetCurrentModeAsync(ct);

    /// <summary>Every mode the OS will accept for this output.</summary>
    public Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default)
        => RequireDisplayMode().GetSupportedModesAsync(ct);

    /// <summary>
    /// Sets the display mode. <paramref name="persist"/> writes it to the
    /// registry so it survives reboots (the provisioning default in the CLI).
    /// </summary>
    public Task SetModeAsync(DisplayMode mode, bool persist = false, CancellationToken ct = default)
        => RequireDisplayMode().SetModeAsync(mode, persist, ct);

    /// <summary>The current rotation of this output.</summary>
    public Task<MonitorOrientation> GetOrientationAsync(CancellationToken ct = default)
        => RequireDisplayMode().GetOrientationAsync(ct);

    /// <summary>
    /// Rotates the output. The landscape/portrait width-height swap is
    /// handled internally — pass the target orientation, nothing else.
    /// </summary>
    public Task SetOrientationAsync(
        MonitorOrientation orientation, bool persist = false, CancellationToken ct = default)
        => RequireDisplayMode().SetOrientationAsync(orientation, persist, ct);

    // -----------------------------------------------------------------------
    // Handle-gated snapshot (ADR-0026 Option D)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens a transient handle, reads the monitor's control capabilities
    /// (plane availability, parsed MCCS capabilities, current mode and
    /// orientation), and closes it. The I/O cost is explicit at this call
    /// site — enumeration never pays it (ADR-0026 Option D).
    /// </summary>
    public static async Task<MonitorSnapshot> ReadCapabilitiesAsync(
        DeviceInfo device, CancellationToken ct = default)
    {
        await using var monitor = await OpenAsync(device, ct).ConfigureAwait(false);

        MccsCapabilities? capabilities = null;
        if (monitor.SupportsVcp)
        {
            try
            {
                capabilities = await monitor.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            }
            catch (MonitorTransferException)
            {
                // The DDC probe passed at open but the full capabilities
                // exchange failed (asleep panel, flaky channel). The plane is
                // still reported; the parsed capabilities are just absent.
            }
        }

        DisplayMode? currentMode = null;
        MonitorOrientation? orientation = null;
        if (monitor.SupportsDisplayMode)
        {
            currentMode = await monitor.GetCurrentModeAsync(ct).ConfigureAwait(false);
            orientation = await monitor.GetOrientationAsync(ct).ConfigureAwait(false);
        }

        return new MonitorSnapshot(
            monitor.SupportsVcp, monitor.SupportsDisplayMode,
            capabilities, currentMode, orientation);
    }

    // -----------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------

    private IMonitorBackend RequireVcp()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _vcp ?? throw new MonitorCapabilityException(
            $"Monitor '{DeviceInfo.Id}' has no DDC/CI channel — VCP control (brightness, "
            + "power, input) is unavailable on this handle. Check SupportsVcp first.",
            DeviceInfo.Id);
    }

    private IDisplayModeBackend RequireDisplayMode()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _displayMode ?? throw new MonitorCapabilityException(
            $"Monitor '{DeviceInfo.Id}' has no display-mode backend on this platform — "
            + "resolution/orientation control is unavailable. Check SupportsDisplayMode first.",
            DeviceInfo.Id);
    }

    /// <summary>Test seam: composes a device over fake backends.</summary>
    internal static MonitorDevice CreateForTest(
        DeviceInfo deviceInfo, IMonitorBackend? vcp, IDisplayModeBackend? displayMode)
        => new(deviceInfo, vcp, displayMode);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_vcp is not null) await _vcp.DisposeAsync().ConfigureAwait(false);
        if (_displayMode is not null) await _displayMode.DisposeAsync().ConfigureAwait(false);
    }
}
