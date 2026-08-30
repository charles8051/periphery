// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Linux;

/// <summary>Linux implementation of the HID transfer surface over <c>/dev/hidrawN</c>.</summary>
/// <remarks>
/// <para>
/// The node is opened <c>O_NONBLOCK</c>; blocking waits are <c>poll(2)</c> on
/// the device fd plus a per-backend <c>eventfd</c> that cancellation and
/// disposal signal to wake a pending wait immediately. Blocking calls run on
/// the thread pool (<see cref="Task.Run(Action)"/>), the same posture the
/// Windows backend takes for feature reports — HID here is a low-rate
/// control-plane surface, not a streaming one.
/// </para>
/// <para>
/// Where Windows gets usage and report lengths from <c>HidP_GetCaps</c>,
/// Linux returns the raw report descriptor (<c>HIDIOCGRDESC</c>) and
/// <see cref="HidReportDescriptor"/> derives them. Whether the device uses
/// numbered reports also comes from the descriptor, because hidraw framing
/// depends on it: <c>read(2)</c> yields a leading report-ID byte only for
/// numbered devices, while <c>write(2)</c> and the feature ioctls always
/// carry one (0 for unnumbered).
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxHidBackend : IHidBackend
{
    private readonly int _fd;
    private readonly int _wakeFd;
    private readonly string _devicePath;
    private readonly bool _usesReportIds;
    private volatile bool _disposed;

    private LinuxHidBackend(
        int fd,
        int wakeFd,
        string devicePath,
        in HidReportDescriptorInfo info)
    {
        _fd = fd;
        _wakeFd = wakeFd;
        _devicePath = devicePath;
        _usesReportIds = info.UsesReportIds;
        UsagePage = info.UsagePage;
        Usage = info.Usage;
        MaxInputReportLength = info.MaxInputPayloadBytes;
        MaxOutputReportLength = info.MaxOutputPayloadBytes;
        MaxFeatureReportLength = info.MaxFeaturePayloadBytes;
    }

    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int MaxInputReportLength { get; }
    public int MaxOutputReportLength { get; }
    public int MaxFeatureReportLength { get; }

    internal static LinuxHidBackend Open(string deviceId)
    {
        string devNode = ResolveDevNode(deviceId);

        int fd = LinuxHidInterop.Open(
            devNode,
            LinuxHidInterop.O_RDWR | LinuxHidInterop.O_NONBLOCK | LinuxHidInterop.O_CLOEXEC);

        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            var inner = new IOException($"open('{devNode}') failed. errno: {errno}");
            throw errno switch
            {
                LinuxHidInterop.EACCES or LinuxHidInterop.EPERM =>
                    new HidAccessDeniedException(
                        $"Access denied opening HID device '{deviceId}' ({devNode}). "
                        + "The calling user lacks read/write permission on the hidraw node — "
                        + "add a udev rule or run with elevated privileges.",
                        inner, deviceId),
                LinuxHidInterop.ENOENT or LinuxHidInterop.ENODEV or LinuxHidInterop.ENXIO =>
                    new HidDeviceNotFoundException(
                        $"HID device '{deviceId}' was not found at {devNode}. "
                        + "It may have been unplugged between enumeration and open.",
                        inner, deviceId),
                _ =>
                    new HidException(
                        $"Failed to open HID device '{deviceId}' ({devNode}). errno: {errno}",
                        inner, deviceId)
            };
        }

        int wakeFd = -1;
        try
        {
            var info = ReadDescriptorInfo(fd, deviceId);

            wakeFd = LinuxHidInterop.EventFd(
                0, LinuxHidInterop.EFD_NONBLOCK | LinuxHidInterop.EFD_CLOEXEC);
            if (wakeFd < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new HidException(
                    $"Failed to create wake eventfd for '{deviceId}'. errno: {errno}",
                    new IOException($"eventfd() failed. errno: {errno}"), deviceId);
            }

            return new LinuxHidBackend(fd, wakeFd, deviceId, info);
        }
        catch
        {
            if (wakeFd >= 0) _ = LinuxHidInterop.Close(wakeFd);
            _ = LinuxHidInterop.Close(fd);
            throw;
        }
    }

    private static unsafe HidReportDescriptorInfo ReadDescriptorInfo(int fd, string deviceId)
    {
        int size = 0;
        if (LinuxHidInterop.Ioctl(fd, LinuxHidInterop.HIDIOCGRDESCSIZE, ref size) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new HidException(
                $"Failed to read HID report-descriptor size for '{deviceId}'. errno: {errno}",
                new IOException($"ioctl(HIDIOCGRDESCSIZE) failed. errno: {errno}"), deviceId);
        }

        size = Math.Clamp(size, 0, LinuxHidInterop.HID_MAX_DESCRIPTOR_SIZE);

        // struct hidraw_report_descriptor { __u32 size; __u8 value[4096]; }
        var raw = new byte[4 + LinuxHidInterop.HID_MAX_DESCRIPTOR_SIZE];
        BitConverter.TryWriteBytes(raw, size);
        fixed (byte* p = raw)
        {
            if (LinuxHidInterop.Ioctl(fd, LinuxHidInterop.HIDIOCGRDESC, p) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new HidException(
                    $"Failed to read HID report descriptor for '{deviceId}'. errno: {errno}",
                    new IOException($"ioctl(HIDIOCGRDESC) failed. errno: {errno}"), deviceId);
            }
        }

        return HidReportDescriptor.Parse(raw.AsSpan(4, size));
    }

    // -----------------------------------------------------------------------
    // Input / output reports
    // -----------------------------------------------------------------------

    public Task<HidReport> ReadReportAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            // Numbered devices prepend the report-ID byte on read; unnumbered
            // devices deliver the payload bare (hidraw framing, see class doc).
            int bufferSize = Math.Max(MaxInputReportLength + 1, 65);
            var buffer = new byte[bufferSize];
            int read = BlockingRead(buffer, ct);

            if (read == 0)
                throw new HidTransferException(
                    "HID read returned 0 bytes — the device was disconnected.",
                    new IOException("Zero-byte read on hidraw fd."));

            return _usesReportIds
                ? new HidReport(buffer[0], buffer.AsMemory(1, read - 1))
                : new HidReport(0, buffer.AsMemory(0, read));
        }, ct);
    }

    public Task WriteReportAsync(HidReport report, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            // hidraw write(2) always carries a leading report-number byte;
            // the kernel strips it for devices without numbered reports.
            var buffer = new byte[report.Data.Length + 1];
            buffer[0] = report.ReportId;
            report.Data.Span.CopyTo(buffer.AsSpan(1));
            BlockingWrite(buffer, ct);
        }, ct);
    }

    private unsafe int BlockingRead(byte[] buffer, CancellationToken ct)
    {
        using var wake = RegisterWake(ct);
        while (true)
        {
            ThrowIfDisposedOrCancelled(ct);

            nint n;
            fixed (byte* p = buffer)
                n = LinuxHidInterop.Read(_fd, p, (nuint)buffer.Length);

            if (n >= 0) return (int)n;

            int errno = Marshal.GetLastPInvokeError();
            if (errno == LinuxHidInterop.EINTR) continue;
            if (errno != LinuxHidInterop.EAGAIN)
                throw TransferError("read", errno);

            WaitForFd(LinuxHidInterop.POLLIN, ct);
        }
    }

    private unsafe void BlockingWrite(byte[] buffer, CancellationToken ct)
    {
        using var wake = RegisterWake(ct);
        while (true)
        {
            ThrowIfDisposedOrCancelled(ct);

            nint n;
            fixed (byte* p = buffer)
                n = LinuxHidInterop.Write(_fd, p, (nuint)buffer.Length);

            if (n >= 0)
            {
                if (n != buffer.Length)
                    throw new HidTransferException(
                        $"Short HID write: {n} of {buffer.Length} bytes.",
                        new IOException("Partial write on hidraw fd."));
                return;
            }

            int errno = Marshal.GetLastPInvokeError();
            if (errno == LinuxHidInterop.EINTR) continue;
            if (errno != LinuxHidInterop.EAGAIN)
                throw TransferError("write", errno);

            WaitForFd(LinuxHidInterop.POLLOUT, ct);
        }
    }

    /// <summary>
    /// Blocks in <c>poll(2)</c> until the device fd is ready for
    /// <paramref name="events"/>, the wake eventfd is signalled
    /// (cancellation/disposal), or the device drops off the bus.
    /// </summary>
    private unsafe void WaitForFd(short events, CancellationToken ct)
    {
        var fds = stackalloc LinuxHidInterop.PollFd[2];
        fds[0] = new LinuxHidInterop.PollFd { Fd = _fd, Events = events };
        fds[1] = new LinuxHidInterop.PollFd { Fd = _wakeFd, Events = LinuxHidInterop.POLLIN };

        int rc = LinuxHidInterop.Poll(fds, 2, timeoutMs: -1);
        if (rc < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == LinuxHidInterop.EINTR) return; // Caller loops.
            throw TransferError("poll", errno);
        }

        if ((fds[1].REvents & LinuxHidInterop.POLLIN) != 0)
        {
            // Woken by cancellation or disposal; drain so the next wait
            // blocks, then let the caller's loop observe the reason.
            LinuxHidInterop.DrainEventFd(_wakeFd);
            return;
        }

        const short gone = LinuxHidInterop.POLLERR | LinuxHidInterop.POLLHUP | LinuxHidInterop.POLLNVAL;
        if ((fds[0].REvents & gone) != 0)
            throw new HidTransferException(
                "HID device dropped off the bus while waiting for I/O readiness.",
                new IOException($"poll() revents: 0x{fds[0].REvents:X4}"));
    }

    private CancellationTokenRegistration RegisterWake(CancellationToken ct) =>
        ct.CanBeCanceled
            ? ct.Register(static state => LinuxHidInterop.SignalEventFd((int)state!), _wakeFd)
            : default;

    private void ThrowIfDisposedOrCancelled(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed)
            throw new ObjectDisposedException(nameof(LinuxHidBackend));
    }

    private HidTransferException TransferError(string op, int errno)
    {
        var inner = new IOException($"{op}() on hidraw fd failed. errno: {errno}");
        return errno is LinuxHidInterop.ENODEV or LinuxHidInterop.EIO or LinuxHidInterop.ENXIO
            ? new HidTransferException(
                $"HID {op} failed — the device may have been disconnected.", inner)
            : new HidTransferException(
                $"HID {op} failed. errno: {errno}", inner);
    }

    // -----------------------------------------------------------------------
    // Feature reports (ADR-0048)
    //
    // HIDIOCGFEATURE / HIDIOCSFEATURE share the Windows framing exactly:
    // buffer[0] carries the report ID in both directions (0 when the device
    // does not use numbered reports), payload follows.
    // -----------------------------------------------------------------------

    public Task<HidReport> ReadFeatureReportAsync(byte reportId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (MaxFeatureReportLength <= 0)
            throw new HidException(
                "Device does not advertise any feature reports (report descriptor "
                + "declares no Feature items).");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var buffer = new byte[MaxFeatureReportLength + 1];
            buffer[0] = reportId;

            int ret = FeatureIoctl(LinuxHidInterop.HidIocGFeature(buffer.Length), buffer);
            if (ret < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                var inner = new IOException(
                    $"ioctl(HIDIOCGFEATURE, reportId=0x{reportId:X2}) failed. errno: {errno}");
                throw new HidTransferException(
                    $"HID feature-report read failed for report 0x{reportId:X2}. " +
                    "The device may not implement this report ID or may have been disconnected.",
                    inner);
            }

            byte respondedId = buffer[0];
            return new HidReport(respondedId, buffer.AsMemory(1, Math.Max(ret - 1, 0)));
        }, ct);
    }

    public Task WriteFeatureReportAsync(HidReport report, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            int length = MaxFeatureReportLength > 0
                ? MaxFeatureReportLength + 1
                : report.Data.Length + 1;
            var buffer = new byte[length];
            buffer[0] = report.ReportId;
            report.Data.Span.CopyTo(buffer.AsSpan(1));

            if (FeatureIoctl(LinuxHidInterop.HidIocSFeature(buffer.Length), buffer) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                var inner = new IOException(
                    $"ioctl(HIDIOCSFEATURE, reportId=0x{report.ReportId:X2}) failed. errno: {errno}");
                throw new HidTransferException(
                    $"HID feature-report write failed for report 0x{report.ReportId:X2}. " +
                    "The device may have rejected the payload or may have been disconnected.",
                    inner);
            }
        }, ct);
    }

    private unsafe int FeatureIoctl(nuint request, byte[] buffer)
    {
        fixed (byte* p = buffer)
            return LinuxHidInterop.Ioctl(_fd, request, p);
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        // Wake any poll(2) waiter so it observes disposal before the fds close.
        LinuxHidInterop.SignalEventFd(_wakeFd);
        _ = LinuxHidInterop.Close(_fd);
        _ = LinuxHidInterop.Close(_wakeFd);
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Device-node resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves an enumeration identity into an openable <c>/dev/hidrawN</c>
    /// node. Periphery's Linux provider surfaces sysfs paths as
    /// <see cref="Periphery.DeviceInfo.Id"/>; depending on the matched
    /// subsystem that path may point at the <c>hid</c> device itself or at a
    /// descendant <c>input</c>/<c>event</c> node, so the walk ascends until a
    /// <c>hidraw/</c> class directory appears. Paths that already name a
    /// <c>/dev/</c> node pass through unchanged.
    /// </summary>
    internal static string ResolveDevNode(string deviceId)
    {
        if (deviceId.StartsWith("/dev/", StringComparison.Ordinal))
            return deviceId;

        // Parity with Windows, where an unresolvable identity surfaces as
        // device-not-found out of the open call rather than a generic error.
        if (!deviceId.StartsWith("/sys/", StringComparison.Ordinal))
            throw new HidDeviceNotFoundException(
                $"HID device '{deviceId}' was not found — the identity is neither a "
                + "sysfs path nor a /dev/hidrawN node.",
                new IOException($"Unrecognized HID device identity: {deviceId}"), deviceId);

        if (!Directory.Exists(deviceId))
            throw new HidDeviceNotFoundException(
                $"HID device '{deviceId}' was not found. "
                + "It may have been unplugged between enumeration and open.",
                new IOException($"sysfs path does not exist: {deviceId}"), deviceId);

        string? current = deviceId.TrimEnd('/');
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string hidrawDir = current + "/hidraw";
            if (Directory.Exists(hidrawDir))
            {
                string? node = Directory.EnumerateDirectories(hidrawDir)
                    .Select(Path.GetFileName)
                    .Where(static n => n is not null && n.StartsWith("hidraw", StringComparison.Ordinal))
                    .OrderBy(static n => n, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (node is not null)
                    return "/dev/" + node;
            }

            current = Path.GetDirectoryName(current)?.Replace('\\', '/');
            if (current is null || current.Length <= "/sys".Length)
                break;
        }

        throw new HidException(
            $"Could not resolve a hidraw node for '{deviceId}'. The device is not "
            + "hidraw-backed (e.g. a PS/2 or virtual input device), or its hidraw "
            + "class node has not been created.", deviceId);
    }
}
