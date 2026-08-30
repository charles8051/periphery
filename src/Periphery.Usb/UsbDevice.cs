// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Periphery.Usb;

/// <summary>
/// A one-shot raw-USB device handle — claim an interface and run control / bulk
/// transfers. Backed by a native per-platform <see cref="IUsbBackend"/> (WinUSB
/// on Windows, libusb-1.0 on Linux; the macOS backend is planned, see ADR-0038).
/// </summary>
/// <remarks>
/// <para>This is the Layer 1 primitive: it has no reconnect behaviour — once the
/// device is unplugged the handle is dead. For a reconnect-resilient handle use
/// <see cref="UsbDeviceProxy"/>, or drive it with
/// <c>DeviceSessionHost&lt;UsbDevice&gt;.StartAsync(profile, UsbDevice.OpenAsync, …)</c>
/// (<see cref="OpenAsync"/> is shaped to be a drop-in <c>createSession</c> factory).</para>
/// </remarks>
public sealed partial class UsbDevice : IAsyncDisposable
{
    private readonly IUsbBackend _backend;
    private readonly ILogger<UsbDevice> _logger;

    // Per-transfer deadline applied to control / bulk-read / bulk-write transfers (not
    // to the perpetual ReadBulkStreamAsync, which legitimately blocks until data
    // arrives). Timeout.InfiniteTimeSpan disables it. A finite value converts a wedged
    // endpoint — a transfer that would otherwise block forever — into a prompt,
    // catchable UsbTimeoutException.
    private readonly TimeSpan _transferTimeout;

    // One transfer in flight per pipe (#263). Neither WinUSB nor libusb serialises
    // concurrent submissions on the same endpoint, and nothing below this layer does
    // either — so before this gate existed the invariant was a *caller convention*
    // held one layer above the resource it protects (TreehopperBoard._comsLock). That
    // left every other consumer — the EFM8 / STM32 bootloaders, the flasher, FrameFlow
    // — inheriting an invariant that was documented nowhere and enforced nowhere.
    //
    // PER ENDPOINT, never per device. ReadBulkStreamAsync legitimately blocks on an IN
    // endpoint until the device sends a packet, so a device-wide gate would have the
    // perpetual pin-report read hold it forever and deadlock every write on the first
    // open. Endpoints are independent pipes; serialising each one is both sufficient
    // and the most that is correct.
    //
    // Created on first use and never disposed, for the reason established in #261/#262:
    // SemaphoreSlim.Dispose is only required once AvailableWaitHandle has been touched
    // (nothing here does), and disposing one that callers may still be parked on turns
    // an ordinary teardown into an ObjectDisposedException inside detached work.
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _pipeGates = new();

    private bool _disposed;

    private UsbDevice(
        DeviceInfo deviceInfo, IUsbBackend backend,
        ILogger<UsbDevice>? logger, TimeSpan transferTimeout)
    {
        DeviceInfo = deviceInfo;
        _backend = backend;
        _logger = logger ?? NullLogger<UsbDevice>.Instance;
        _transferTimeout = transferTimeout;
    }

    /// <summary>
    /// Test-only factory that constructs a device over an injected backend,
    /// bypassing the OS-specific open path. Used by Periphery.Usb.Tests to
    /// exercise the public surface against a fake <see cref="IUsbBackend"/>.
    /// </summary>
    internal static UsbDevice CreateForTest(
        DeviceInfo deviceInfo, IUsbBackend backend,
        ILogger<UsbDevice>? logger = null, TimeSpan? transferTimeout = null)
        => new(deviceInfo, backend, logger, transferTimeout ?? Timeout.InfiniteTimeSpan);

    /// <summary>The discovery snapshot this device was opened from.</summary>
    public DeviceInfo DeviceInfo { get; }

    /// <summary>The standard USB device descriptor, read at open.</summary>
    public UsbDeviceDescriptor Descriptor => _backend.DeviceDescriptor;

    /// <summary>The active configuration descriptor (interfaces + endpoints), read at open.</summary>
    public UsbConfigurationDescriptor Configuration => _backend.Configuration;

    /// <summary>
    /// Claims a device interface for I/O. Interface 0 is claimed implicitly at
    /// open; call this only for additional interfaces.
    /// </summary>
    public void ClaimInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.ClaimInterface(interfaceNumber);
    }

    /// <summary>Releases a previously claimed interface.</summary>
    public void ReleaseInterface(byte interfaceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.ReleaseInterface(interfaceNumber);
    }

    /// <summary>
    /// Runs a control transfer on endpoint 0. The data stage, if any, is read
    /// into or written from <paramref name="buffer"/> per the direction bit of
    /// <see cref="UsbControlSetup.RequestType"/>.
    /// </summary>
    /// <returns>The number of bytes transferred in the data stage.</returns>
    public Task<int> ControlTransferAsync(UsbControlSetup setup, Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RunTransferAsync("control", 0, _transferTimeout,
            c => _backend.ControlTransferAsync(setup, buffer, c), ct);
    }

    /// <summary>Reads up to <paramref name="count"/> bytes from a bulk / interrupt IN endpoint.</summary>
    /// <returns>The bytes actually read (length ≤ <paramref name="count"/>).</returns>
    public async Task<byte[]> BulkReadAsync(byte endpointAddress, int count, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var buffer = new byte[count];
        int read = await RunTransferAsync("bulk read", endpointAddress, _transferTimeout,
            c => _backend.BulkReadAsync(endpointAddress, buffer, c), ct).ConfigureAwait(false);
        return read == count ? buffer : buffer[..read];
    }

    /// <summary>Writes <paramref name="data"/> to a bulk / interrupt OUT endpoint.</summary>
    /// <returns>The number of bytes written.</returns>
    /// <param name="onIssued">
    /// Invoked once the transfer has cleared the pipe gate and is about to be handed to the
    /// backend — the moment after which a failure no longer proves the bytes never left. A
    /// caller that must distinguish "never issued" from "issued and then faulted" cannot infer
    /// it from the exception: a cancellation or a deadline expiring while still queued on the
    /// gate surfaces exactly like one that aborted a transfer already in flight (#263 item 3).
    /// </param>
    public Task<int> BulkWriteAsync(
        byte endpointAddress, ReadOnlyMemory<byte> data, CancellationToken ct = default,
        Action? onIssued = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RunTransferAsync("bulk write", endpointAddress, _transferTimeout,
            c => _backend.BulkWriteAsync(endpointAddress, data, c), ct, onIssued);
    }

    /// <summary>
    /// Continuously reads a bulk / interrupt IN endpoint, yielding each transfer's
    /// payload until <paramref name="ct"/> is cancelled or the device disconnects.
    /// </summary>
    /// <remarks>
    /// Backed by the overlapped <see cref="BulkReadAsync(byte, int, CancellationToken)"/>:
    /// cancellation aborts the in-flight transfer via <c>CancelIoEx</c> rather than
    /// blocking a thread or waiting on a timeout. Each element is a freshly allocated
    /// buffer sliced to the bytes read (a pooled / zero-copy variant is a follow-up).
    /// </remarks>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadBulkStreamAsync(
        byte endpointAddress,
        int packetSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packetSize);

        while (!ct.IsCancellationRequested)
        {
            var buffer = new byte[packetSize];
            int read;
            try
            {
                // No deadline on the stream read: a pin-report / interrupt IN endpoint
                // legitimately blocks until the device emits the next packet, so the
                // transfer watchdog is explicitly disabled here (Infinite).
                read = await RunTransferAsync("stream read", endpointAddress, Timeout.InfiniteTimeSpan,
                    c => _backend.BulkReadAsync(endpointAddress, buffer, c), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (read > 0)
                yield return new ReadOnlyMemory<byte>(buffer, 0, read);
        }
    }

    /// <summary>
    /// Opens the USB device identified by <paramref name="deviceInfo"/>. The
    /// signature matches <c>Func&lt;DeviceInfo, CancellationToken, Task&lt;UsbDevice&gt;&gt;</c>
    /// so it can be passed directly as the <c>createSession</c> factory to
    /// <c>DeviceSessionHost&lt;UsbDevice&gt;.StartAsync</c>.
    /// </summary>
    public static Task<UsbDevice> OpenAsync(DeviceInfo deviceInfo, CancellationToken ct = default)
        => OpenAsync(deviceInfo, ct, transferTimeout: null, logger: null);

    /// <summary>
    /// Opens the device with observability options. <paramref name="transferTimeout"/> is
    /// a per-transfer deadline (control / bulk read / bulk write) after which a wedged
    /// transfer faults with <see cref="UsbTimeoutException"/> instead of blocking forever;
    /// <see langword="null"/> disables it (the default
    /// <see cref="OpenAsync(DeviceInfo, CancellationToken)"/> behaviour). It does
    /// <b>not</b> apply to <see cref="ReadBulkStreamAsync"/>, which legitimately blocks
    /// until the device sends data. <paramref name="logger"/> receives per-transfer Trace
    /// logs plus timeout / failure diagnostics.
    /// </summary>
    public static Task<UsbDevice> OpenAsync(
        DeviceInfo deviceInfo, CancellationToken ct, TimeSpan? transferTimeout, ILogger<UsbDevice>? logger)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInfo.Id);
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
            return OpenWindowsAsync(deviceInfo, transferTimeout ?? Timeout.InfiniteTimeSpan, logger, ct);

        if (OperatingSystem.IsLinux())
            return OpenLinuxAsync(deviceInfo, transferTimeout ?? Timeout.InfiniteTimeSpan, logger, ct);

        throw new PlatformNotSupportedException(
            $"UsbDevice.OpenAsync is not yet implemented on {Environment.OSVersion.Platform}. " +
            "The macOS (libusb) backend is planned (ADR-0038).");
    }

    [SupportedOSPlatform("windows")]
    private static Task<UsbDevice> OpenWindowsAsync(
        DeviceInfo deviceInfo, TimeSpan transferTimeout, ILogger<UsbDevice>? logger, CancellationToken ct)
    {
        // WinUSB open is synchronous at the OS level (CreateFile + WinUsb_Initialize);
        // wrap in Task.Run so we don't block the caller's thread.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var backend = Windows.WinUsbBackend.Open(deviceInfo.Id);
            return new UsbDevice(deviceInfo, backend, logger, transferTimeout);
        }, ct);
    }

    [SupportedOSPlatform("linux")]
    private static Task<UsbDevice> OpenLinuxAsync(
        DeviceInfo deviceInfo, TimeSpan transferTimeout, ILogger<UsbDevice>? logger, CancellationToken ct)
    {
        // The usbfs open + libusb_wrap_sys_device + descriptor reads are
        // synchronous; wrap in Task.Run so we don't block the caller's thread.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var backend = Linux.LibUsbBackend.Open(deviceInfo.Id);
            return new UsbDevice(deviceInfo, backend, logger, transferTimeout);
        }, ct);
    }

    // ── Transfer instrumentation funnel ────────────────────────────────

    /// <summary>
    /// The single funnel every transfer flows through: records latency / counts to the
    /// <see cref="UsbMeters"/> instruments, emits a Trace (success) / Warning (timeout) /
    /// Error (failure) log, and — when <paramref name="timeout"/> is finite — enforces a
    /// deadline by cancelling the in-flight transfer (the backend aborts the native I/O
    /// via <c>CancelIoEx</c>) and translating the resulting cancellation into a
    /// <see cref="UsbTimeoutException"/>. A caller-requested cancellation
    /// (<paramref name="ct"/>) still surfaces as <see cref="OperationCanceledException"/> —
    /// it is not a fault.
    /// </summary>
    private async Task<int> RunTransferAsync(
        string op, byte endpoint, TimeSpan timeout,
        Func<CancellationToken, Task<int>> transfer, CancellationToken ct,
        Action? onIssued = null)
    {
        CancellationTokenSource? timeoutCts = null;
        CancellationToken effectiveCt = ct;
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            effectiveCt = timeoutCts.Token;
        }

        var gate = _pipeGates.GetOrAdd(endpoint, static _ => new SemaphoreSlim(1, 1));
        bool held = false;
        long startTs = 0;
        UsbMeters.QueuedTransfers.Add(1);
        try
        {
            // The deadline deliberately spans queueing AND the transfer, rather than
            // starting once the pipe is free. A caller queued behind a wedged transfer
            // would otherwise wait without bound — and the alternative, an ungated wait
            // honouring only the caller's token, reintroduces exactly the unbounded hang
            // this timeout exists to prevent. Duration is still measured from the wire,
            // so the latency metric keeps meaning what it did.
            await gate.WaitAsync(effectiveCt).ConfigureAwait(false);
            held = true;

            // Counted from HERE, not from method entry (#263 review). InFlightTransfers
            // means "the backend is working on this"; a caller still queued on the gate
            // has not been issued, and folding it in would report ten in-flight transfers
            // where the hardware has one. Queue depth is its own instrument.
            UsbMeters.QueuedTransfers.Add(-1);
            UsbMeters.InFlightTransfers.Add(1);
            startTs = Stopwatch.GetTimestamp();

            // Same instant as the in-flight counter, and for the same reason: this is where
            // "queued" becomes "the backend is working on it".
            //
            // Guarded on exactly what the backends refuse at their own entry — a cancelled
            // token, a disposed device — so the callback does not claim an issue that the
            // very next call will decline. Nothing awaits between here and `transfer`, and
            // .NET cancellation is not preemptive, so what remains is the token firing
            // between this check and the backend's: adjacent instructions on one thread.
            // Closing that last sliver would mean threading the callback through
            // IUsbBackend into both native backends and every fake, to move a guard a few
            // instructions earlier (#271 review turn 7).
            if (!_disposed && !effectiveCt.IsCancellationRequested)
                onIssued?.Invoke();

            int transferred = await transfer(effectiveCt).ConfigureAwait(false);
            double ms = Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
            UsbMeters.TransfersTotal.Add(1);
            UsbMeters.TransferDuration.Record(ms);
            LogTransferCompleted(_logger, op, endpoint, transferred, ms);
            return transferred;
        }
        catch (OperationCanceledException)
            when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            // The deadline fired, not the caller — the endpoint is wedged.
            UsbMeters.TransferErrorsTotal.Add(1);
            LogTransferTimedOut(_logger, op, endpoint, timeout.TotalMilliseconds);
            throw new UsbTimeoutException(
                $"USB {op} on endpoint 0x{endpoint:X2} did not complete within {timeout.TotalMilliseconds:F0} ms — " +
                "the endpoint is likely wedged (a firmware hang that stopped draining it, or an unplugged device).",
                DeviceInfo.Id, timeout);
        }
        catch (OperationCanceledException)
        {
            throw; // caller-requested cancellation — propagate as-is
        }
        catch (Exception ex)
        {
            UsbMeters.TransferErrorsTotal.Add(1);
            LogTransferFailed(_logger, op, endpoint, ex);
            throw;
        }
        finally
        {
            if (held)
            {
                UsbMeters.InFlightTransfers.Add(-1);
                gate.Release();
            }
            else
            {
                // Never admitted: still counted as queued, so undo that instead.
                UsbMeters.QueuedTransfers.Add(-1);
            }

            timeoutCts?.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    // ── Source-generated log methods ───────────────────────────────────

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "USB {Operation} ep=0x{Endpoint:X2}: {Bytes} bytes in {ElapsedMs:F2} ms")]
    private static partial void LogTransferCompleted(
        ILogger logger, string operation, byte endpoint, int bytes, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "USB {Operation} ep=0x{Endpoint:X2} timed out after {TimeoutMs:F0} ms — endpoint may be wedged")]
    private static partial void LogTransferTimedOut(
        ILogger logger, string operation, byte endpoint, double timeoutMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "USB {Operation} ep=0x{Endpoint:X2} failed")]
    private static partial void LogTransferFailed(
        ILogger logger, string operation, byte endpoint, Exception ex);
}
