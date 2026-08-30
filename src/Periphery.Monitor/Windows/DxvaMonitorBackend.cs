// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor.Windows;

/// <summary>
/// Windows implementation of the VCP plane over the <b>low-level</b> dxva2
/// physical-monitor API (<c>GetVCPFeatureAndVCPFeatureReply</c> /
/// <c>SetVCPFeature</c> / <c>CapabilitiesRequestAndCapabilitiesReply</c>).
/// The high-level <c>GetMonitorBrightness</c> family is deliberately unused
/// (ADR-0058 D3): all semantics live in the shared layer so the platforms
/// cannot drift.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DxvaMonitorBackend : IMonitorBackend
{
    private readonly IntPtr _physicalMonitor;
    private readonly string _deviceId;
    private bool _disposed;

    private DxvaMonitorBackend(IntPtr physicalMonitor, string deviceId)
    {
        _physicalMonitor = physicalMonitor;
        _deviceId = deviceId;
    }

    /// <summary>
    /// Acquires the physical monitor behind <paramref name="hMonitor"/> and
    /// probes for a live DDC/CI channel. Returns <see langword="null"/> when
    /// the channel is absent (virtual displays, DDC-disabled panels) — the
    /// plane is then simply not present on the handle, per ADR-0058 D7.
    /// </summary>
    internal static unsafe DxvaMonitorBackend? TryOpen(IntPtr hMonitor, string deviceId)
    {
        if (!MonitorInterop.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count)
            || count == 0)
            return null;

        // One HMONITOR usually carries exactly one physical monitor; in a
        // duplicated topology it can carry several. v1 takes the first —
        // the ADR documents the rarity; revisit with index correlation if a
        // real clone-mode consumer appears.
        var monitors = new MonitorInterop.PhysicalMonitor[count];
        fixed (MonitorInterop.PhysicalMonitor* p = monitors)
        {
            if (!MonitorInterop.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, p))
                return null;
        }

        IntPtr handle = monitors[0].Handle;
        for (int i = 1; i < monitors.Length; i++)
            _ = MonitorInterop.DestroyPhysicalMonitor(monitors[i].Handle);

        // DDC/CI presence probe. Real multi-monitor adapters share one DDC
        // mux and individual exchanges fail transiently
        // (ERROR_GRAPHICS_DDCCI_* bus noise — observed on a 3-panel bench),
        // so the probe retries the capabilities-length handshake and falls
        // back to a direct VCP read before declaring the plane absent.
        bool probed = Retry(() =>
            MonitorInterop.GetCapabilitiesStringLength(handle, out uint capsLength)
            && capsLength > 0);
        if (!probed)
            probed = Retry(() =>
                MonitorInterop.GetVCPFeatureAndVCPFeatureReply(
                    handle, VcpCode.Luminance, out _, out _, out _));

        if (!probed)
        {
            _ = MonitorInterop.DestroyPhysicalMonitor(handle);
            return null;
        }

        return new DxvaMonitorBackend(handle, deviceId);
    }

    public Task<VcpFeatureValue> GetVcpFeatureAsync(byte code, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // dxva2 VCP calls are synchronous and can take tens of milliseconds
        // (the OS serializes the DDC channel); hop to the pool like the other
        // backends do for control-plane ioctls.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            uint current = 0, max = 0;
            if (!Retry(() => MonitorInterop.GetVCPFeatureAndVCPFeatureReply(
                    _physicalMonitor, code, out _, out current, out max)))
            {
                throw TransferError($"reading VCP 0x{code:X2}");
            }
            return new VcpFeatureValue((ushort)current, (ushort)max);
        }, ct);
    }

    public Task SetVcpFeatureAsync(byte code, ushort value, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Retry(() => MonitorInterop.SetVCPFeature(_physicalMonitor, code, value)))
                throw TransferError($"writing VCP 0x{code:X2} = {value}");
        }, ct);
    }

    public Task<string> GetCapabilitiesStringAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!MonitorInterop.GetCapabilitiesStringLength(_physicalMonitor, out uint length)
                || length == 0)
            {
                throw TransferError("reading the capabilities-string length");
            }

            var buffer = new byte[length];
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    if (!MonitorInterop.CapabilitiesRequestAndCapabilitiesReply(
                            _physicalMonitor, p, length))
                    {
                        throw TransferError("reading the capabilities string");
                    }
                }
            }

            // ASCII, NUL-terminated.
            int end = Array.IndexOf(buffer, (byte)0);
            return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }, ct);
    }

    /// <summary>
    /// Bounded retry for one dxva2 DDC exchange. The bus is shared and noisy
    /// on multi-monitor adapters; three attempts with a short quiet gap
    /// (mirroring ddcutil's discipline) absorbs the transient failures
    /// without masking a genuinely absent channel.
    /// </summary>
    private static bool Retry(Func<bool> exchange)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (exchange())
                return true;
            if (attempt >= 2)
                return false;
            Thread.Sleep(60);
        }
    }

    private MonitorTransferException TransferError(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new MonitorTransferException(
            $"DDC/CI {operation} failed for '{_deviceId}'. Win32 error: {error}. "
            + "The monitor may be asleep, switched to another input, or mid-hot-plug.",
            new System.ComponentModel.Win32Exception(error), _deviceId);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _ = MonitorInterop.DestroyPhysicalMonitor(_physicalMonitor);
        return ValueTask.CompletedTask;
    }
}
