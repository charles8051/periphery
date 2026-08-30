// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor.Linux;

/// <summary>
/// Linux implementation of the VCP plane: DDC/CI (MCCS over DDC2Bi) spoken
/// directly to the panel through the connector's I2C bus. The frame protocol
/// is the pure <see cref="DdcCiWire"/> codec; this shell owns the fd, the
/// slave-address ioctl, and the mandatory inter-command quiet time
/// (pure cadence state advanced by a shell-owned clock — ADR-0058 D4).
/// </summary>
/// <remarks>
/// Identity resolution follows ADR-0057 D2: the enumeration identity is the
/// DRM connector syspath (<c>…/drm/card1/card1-HDMI-A-1</c>) whose
/// <c>ddc</c> symlink names the adapter → <c>/dev/i2c-N</c>. A
/// <c>/dev/i2c-…</c> identity passes through verbatim. All commands are
/// serialized on one mutex — the channel is half-duplex and timing-bound.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class I2cDdcMonitorBackend : IMonitorBackend
{
    private const int MaxCapabilitiesLength = 4096; // Defensive bound; real strings are < 1 KiB.

    private readonly int _fd;
    private readonly string _deviceId;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private long _nextCommandAt; // Stopwatch timestamp gate for command spacing.
    private volatile bool _disposed;

    private I2cDdcMonitorBackend(int fd, string deviceId)
    {
        _fd = fd;
        _deviceId = deviceId;
    }

    /// <summary>
    /// Opens the connector's DDC channel, or returns <see langword="null"/>
    /// when the connector exposes no <c>ddc</c> link (virtual GPUs, eDP
    /// panels without DDC routing) — the plane is then absent on the handle.
    /// Permission and not-found failures throw; they are not "absent".
    /// </summary>
    internal static I2cDdcMonitorBackend? TryOpen(string deviceId)
    {
        string? devNode = ResolveDevNode(deviceId);
        if (devNode is null)
            return null;

        int fd = I2cInterop.Open(devNode, I2cInterop.O_RDWR | I2cInterop.O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            var inner = new IOException($"open('{devNode}') failed. errno: {errno}");
            throw errno switch
            {
                I2cInterop.EACCES or I2cInterop.EPERM =>
                    new MonitorAccessDeniedException(
                        $"Access denied opening the DDC channel for '{deviceId}' ({devNode}). "
                        + "Join the 'i2c' group or add a udev rule for i2c-dev nodes.",
                        inner, deviceId),
                I2cInterop.ENOENT or I2cInterop.ENODEV or I2cInterop.ENXIO =>
                    new MonitorDeviceNotFoundException(
                        $"DDC channel for '{deviceId}' was not found at {devNode}. "
                        + "The monitor may have been unplugged. Is the i2c-dev module loaded?",
                        inner, deviceId),
                _ => new MonitorException(
                    $"Failed to open the DDC channel for '{deviceId}' ({devNode}). errno: {errno}",
                    inner, deviceId),
            };
        }

        if (I2cInterop.Ioctl(fd, I2cInterop.I2C_SLAVE, I2cInterop.DdcSlaveAddress) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            _ = I2cInterop.Close(fd);
            throw new MonitorException(
                $"Binding the DDC slave address (0x37) failed for '{deviceId}' ({devNode}). "
                + $"errno: {errno}",
                new IOException($"ioctl(I2C_SLAVE) failed. errno: {errno}"), deviceId);
        }

        return new I2cDdcMonitorBackend(fd, deviceId);
    }

    // -----------------------------------------------------------------------
    // VCP surface
    // -----------------------------------------------------------------------

    public Task<VcpFeatureValue> GetVcpFeatureAsync(byte code, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return RunCommandAsync(async ct2 =>
        {
            WriteFrame(DdcCiWire.EncodeGetVcp(code));
            await Task.Delay(DdcCiWire.ReplyDelay, ct2).ConfigureAwait(false);

            var reply = ReadFrame(DdcCiWire.GetVcpReplyLength);
            if (!DdcCiWire.TryDecodeGetVcpReply(reply, code, out var value, out string? error))
                throw new MonitorTransferException(
                    $"DDC/CI read of VCP 0x{code:X2} failed for '{_deviceId}': {error}.",
                    _deviceId);
            return value;
        }, ct);
    }

    public Task SetVcpFeatureAsync(byte code, ushort value, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return RunCommandAsync<object?>(ct2 =>
        {
            WriteFrame(DdcCiWire.EncodeSetVcp(code, value));
            return Task.FromResult<object?>(null);
        }, ct);
    }

    public Task<string> GetCapabilitiesStringAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return RunCommandAsync(async ct2 =>
        {
            var caps = new StringBuilder();
            ushort offset = 0;
            while (caps.Length < MaxCapabilitiesLength)
            {
                WriteFrame(DdcCiWire.EncodeCapabilitiesRequest(offset));
                await Task.Delay(DdcCiWire.ReplyDelay, ct2).ConfigureAwait(false);

                var reply = ReadFrame(64); // addr+len+op+offset(2)+32 data+chk fits well within 64.
                if (!DdcCiWire.TryDecodeCapabilitiesFragment(
                        reply, out ushort echoedOffset, out byte[] data, out string? error))
                {
                    throw new MonitorTransferException(
                        $"DDC/CI capabilities read failed for '{_deviceId}' at offset {offset}: {error}.",
                        _deviceId);
                }

                if (data.Length == 0)
                    break; // End of string.
                if (echoedOffset != offset)
                    throw new MonitorTransferException(
                        $"DDC/CI capabilities read for '{_deviceId}' echoed offset {echoedOffset}, "
                        + $"expected {offset} — fragments out of sync.", _deviceId);

                caps.Append(Encoding.ASCII.GetString(data));
                offset += (ushort)data.Length;

                // Fragments are themselves commands; respect spacing between them.
                await WaitForCommandWindowAsync(ct2).ConfigureAwait(false);
            }
            return caps.ToString();
        }, ct);
    }

    // -----------------------------------------------------------------------
    // Channel mechanics
    // -----------------------------------------------------------------------

    /// <summary>
    /// Serializes a command on the half-duplex channel and enforces the
    /// MCCS inter-command quiet time before it starts.
    /// </summary>
    private async Task<T> RunCommandAsync<T>(Func<CancellationToken, Task<T>> command, CancellationToken ct)
    {
        await _channelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForCommandWindowAsync(ct).ConfigureAwait(false);
            try
            {
                return await command(ct).ConfigureAwait(false);
            }
            finally
            {
                _nextCommandAt = Stopwatch.GetTimestamp()
                    + (long)(DdcCiWire.CommandSpacing.TotalSeconds * Stopwatch.Frequency);
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task WaitForCommandWindowAsync(CancellationToken ct)
    {
        long now = Stopwatch.GetTimestamp();
        long gate = Volatile.Read(ref _nextCommandAt);
        if (now >= gate)
            return;
        var wait = TimeSpan.FromSeconds((gate - now) / (double)Stopwatch.Frequency);
        await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    private unsafe void WriteFrame(byte[] frame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(I2cDdcMonitorBackend));

        nint written;
        fixed (byte* p = frame)
            written = I2cInterop.Write(_fd, p, (nuint)frame.Length);

        if (written != frame.Length)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw MapIoError("write", errno);
        }
    }

    private unsafe byte[] ReadFrame(int length)
    {
        var buffer = new byte[length];
        nint read;
        fixed (byte* p = buffer)
            read = I2cInterop.Read(_fd, p, (nuint)length);

        if (read <= 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw MapIoError("read", errno);
        }
        return read == length ? buffer : buffer[..(int)read];
    }

    private MonitorException MapIoError(string op, int errno)
    {
        var inner = new IOException($"{op}() on the DDC i2c fd failed. errno: {errno}");
        return errno switch
        {
            I2cInterop.ENODEV or I2cInterop.ENXIO =>
                new MonitorDeviceLostException(
                    $"The monitor behind '{_deviceId}' stopped answering DDC ({op}, errno {errno}) — "
                    + "it may have been unplugged or powered off.", inner, _deviceId),
            I2cInterop.EREMOTEIO or I2cInterop.ETIMEDOUT or I2cInterop.EIO =>
                new MonitorTransferException(
                    $"DDC/CI {op} failed for '{_deviceId}' (errno {errno}). The panel may be "
                    + "asleep, switched away from this input, or DDC/CI may be disabled in its OSD.",
                    inner, _deviceId),
            _ => new MonitorTransferException(
                $"DDC/CI {op} failed for '{_deviceId}'. errno: {errno}", inner, _deviceId),
        };
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _ = I2cInterop.Close(_fd);
        _channelLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Identity resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a DRM connector syspath to its DDC i2c node via the
    /// connector's <c>ddc</c> symlink. Returns <see langword="null"/> when
    /// the identity is a connector without a DDC link (plane absent);
    /// throws <see cref="MonitorDeviceNotFoundException"/> when the identity
    /// itself is unrecognizable or gone — same classification rules as the
    /// ADR-0057 backends.
    /// </summary>
    internal static string? ResolveDevNode(string deviceId)
    {
        if (deviceId.StartsWith("/dev/i2c-", StringComparison.Ordinal))
            return deviceId;

        if (!deviceId.StartsWith("/sys/", StringComparison.Ordinal))
            throw new MonitorDeviceNotFoundException(
                $"Monitor '{deviceId}' was not found — the identity is neither a DRM "
                + "connector syspath nor a /dev/i2c-N node.", deviceId);

        string trimmed = deviceId.TrimEnd('/');
        if (!Directory.Exists(trimmed))
            throw new MonitorDeviceNotFoundException(
                $"Monitor '{deviceId}' was not found. It may have been unplugged between "
                + "enumeration and open.",
                new IOException($"sysfs path does not exist: {deviceId}"), deviceId);

        string ddcLink = trimmed + "/ddc";
        if (!Directory.Exists(ddcLink))
            return null; // Connector has no DDC routing — VCP plane absent.

        // The link names the adapter directory (".../i2c-5"); its basename
        // carries the bus number.
        var target = new DirectoryInfo(ddcLink).ResolveLinkTarget(returnFinalTarget: true);
        string name = Path.GetFileName((target?.FullName ?? ddcLink).TrimEnd('/'));
        if (!name.StartsWith("i2c-", StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int bus))
            return null;

        return $"/dev/i2c-{bus}";
    }
}
