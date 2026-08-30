// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor.Windows;

/// <summary>
/// Windows implementation of the display-mode plane over
/// <c>EnumDisplaySettingsEx</c> / <c>ChangeDisplaySettingsEx</c> on the
/// path's source GDI device (ADR-0058 D8). Every set is probed with
/// <c>CDS_TEST</c> before committing; <c>persist</c> maps to
/// <c>CDS_UPDATEREGISTRY</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GdiDisplayModeBackend : IDisplayModeBackend
{
    private readonly string _sourceGdiName;
    private readonly string _deviceId;
    private bool _disposed;

    internal GdiDisplayModeBackend(string sourceGdiName, string deviceId)
    {
        _sourceGdiName = sourceGdiName;
        _deviceId = deviceId;
    }

    public Task<DisplayMode> GetCurrentModeAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var devMode = ReadCurrent();
            return new DisplayMode(
                (int)devMode.PelsWidth, (int)devMode.PelsHeight, (int)devMode.DisplayFrequency);
        }, ct);
    }

    public Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run<IReadOnlyList<DisplayMode>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Modes repeat per color depth; current-depth 32bpp is the only
            // depth modern Windows actually sets, so dedupe on the triple.
            var seen = new HashSet<(int, int, int)>();
            var modes = new List<DisplayMode>();
            var devMode = MonitorInterop.DevMode.Create();
            for (int i = 0; MonitorInterop.EnumDisplaySettingsEx(_sourceGdiName, i, ref devMode, 0); i++)
            {
                var key = ((int)devMode.PelsWidth, (int)devMode.PelsHeight, (int)devMode.DisplayFrequency);
                if (seen.Add(key))
                    modes.Add(new DisplayMode(key.Item1, key.Item2, key.Item3));
            }

            if (modes.Count == 0)
                throw new MonitorTransferException(
                    $"'{_sourceGdiName}' enumerated no display modes for '{_deviceId}'.",
                    _deviceId);
            return modes;
        }, ct);
    }

    public Task SetModeAsync(DisplayMode mode, bool persist, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mode);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var devMode = MonitorInterop.DevMode.Create();
            devMode.PelsWidth = (uint)mode.Width;
            devMode.PelsHeight = (uint)mode.Height;
            devMode.Fields = MonitorInterop.DM_PELSWIDTH | MonitorInterop.DM_PELSHEIGHT;
            if (mode.RefreshRateHz > 0)
            {
                devMode.DisplayFrequency = (uint)mode.RefreshRateHz;
                devMode.Fields |= MonitorInterop.DM_DISPLAYFREQUENCY;
            }

            Apply(ref devMode, persist, $"mode {mode}");
        }, ct);
    }

    public Task<MonitorOrientation> GetOrientationAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return CcdOrientation.FromDevMode(ReadCurrent().DisplayOrientation);
        }, ct);
    }

    public Task SetOrientationAsync(MonitorOrientation orientation, bool persist, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var current = ReadCurrent();
            var from = CcdOrientation.FromDevMode(current.DisplayOrientation);
            if (from == orientation)
                return;

            // The DEVMODE dimensions describe the post-rotation frame: a
            // landscape↔portrait crossing swaps them (OrientationMath owns
            // that decision; classic rotation bug otherwise).
            (int width, int height) = OrientationMath.Reframe(
                (int)current.PelsWidth, (int)current.PelsHeight, from, orientation);

            var devMode = MonitorInterop.DevMode.Create();
            devMode.PelsWidth = (uint)width;
            devMode.PelsHeight = (uint)height;
            devMode.DisplayOrientation = CcdOrientation.ToDevMode(orientation);
            devMode.Fields = MonitorInterop.DM_PELSWIDTH
                | MonitorInterop.DM_PELSHEIGHT
                | MonitorInterop.DM_DISPLAYORIENTATION;

            Apply(ref devMode, persist, $"orientation {orientation}");
        }, ct);
    }

    private MonitorInterop.DevMode ReadCurrent()
    {
        var devMode = MonitorInterop.DevMode.Create();
        if (!MonitorInterop.EnumDisplaySettingsEx(
                _sourceGdiName, MonitorInterop.ENUM_CURRENT_SETTINGS, ref devMode, 0))
        {
            throw new MonitorTransferException(
                $"Reading the current display mode of '{_sourceGdiName}' failed for '{_deviceId}'.",
                _deviceId);
        }
        return devMode;
    }

    private void Apply(ref MonitorInterop.DevMode devMode, bool persist, string what)
    {
        // Probe first: CDS_TEST validates without flashing the screen, so an
        // unsupported request fails cleanly instead of half-applying.
        int rc = MonitorInterop.ChangeDisplaySettingsEx(
            _sourceGdiName, ref devMode, IntPtr.Zero, MonitorInterop.CDS_TEST, IntPtr.Zero);
        if (rc != MonitorInterop.DISP_CHANGE_SUCCESSFUL)
            throw ChangeError(rc, what, probe: true);

        uint flags = persist ? MonitorInterop.CDS_UPDATEREGISTRY : 0;
        rc = MonitorInterop.ChangeDisplaySettingsEx(
            _sourceGdiName, ref devMode, IntPtr.Zero, flags, IntPtr.Zero);
        if (rc != MonitorInterop.DISP_CHANGE_SUCCESSFUL)
            throw ChangeError(rc, what, probe: false);
    }

    private MonitorException ChangeError(int rc, string what, bool probe)
    {
        string stage = probe ? "rejected by the CDS_TEST probe" : "failed to apply";
        string detail = rc switch
        {
            MonitorInterop.DISP_CHANGE_BADMODE =>
                "the display does not support this mode (DISP_CHANGE_BADMODE)",
            MonitorInterop.DISP_CHANGE_RESTART =>
                "Windows requires a restart for this change (DISP_CHANGE_RESTART)",
            MonitorInterop.DISP_CHANGE_BADFLAGS or MonitorInterop.DISP_CHANGE_BADPARAM =>
                $"invalid parameters (code {rc})",
            _ => $"ChangeDisplaySettingsEx returned {rc}",
        };
        return new MonitorTransferException(
            $"Setting {what} on '{_sourceGdiName}' {stage}: {detail}.", _deviceId);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask; // GDI device names are not handles.
    }
}
