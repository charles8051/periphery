// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Periphery.Hid.Windows;

/// <summary>Windows implementation of the HID transfer surface.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHidBackend : IHidBackend
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly int _inputReportLength;
    private readonly int _outputReportLength;
    private readonly int _featureReportLength;
    private bool _disposed;

    private WindowsHidBackend(
        SafeFileHandle handle,
        FileStream stream,
        ushort usagePage,
        ushort usage,
        int inputReportLength,
        int outputReportLength,
        int featureReportLength)
    {
        _handle = handle;
        _stream = stream;
        UsagePage = usagePage;
        Usage = usage;
        _inputReportLength = inputReportLength;
        _outputReportLength = outputReportLength;
        _featureReportLength = featureReportLength;
    }

    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int MaxInputReportLength => _inputReportLength > 0 ? _inputReportLength - 1 : 0;
    public int MaxOutputReportLength => _outputReportLength > 0 ? _outputReportLength - 1 : 0;
    public int MaxFeatureReportLength => _featureReportLength > 0 ? _featureReportLength - 1 : 0;

    // Win32 error codes used for exception classification
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;

    internal static WindowsHidBackend Open(string devicePath)
    {
        // Periphery enumeration surfaces SetupAPI device-instance IDs
        // (e.g. "HID\VID_0665&PID_5161\6&1B6066C6&0&0000"), but
        // CreateFile needs the device-interface path
        // (\\?\HID#...#{GUID_DEVINTERFACE_HID}). Resolve the former into
        // the latter via cfgmgr32 before opening. Inputs that already
        // look like an interface path (begin with \\?\ or \\.\) pass
        // through unchanged so consumers that constructed a path
        // themselves still work.
        string resolvedPath = ResolveInterfacePath(devicePath);

        var handle = HidInterop.CreateFile(
            resolvedPath,
            HidInterop.GENERIC_READ | HidInterop.GENERIC_WRITE,
            HidInterop.FILE_SHARE_READ | HidInterop.FILE_SHARE_WRITE,
            nint.Zero,
            HidInterop.OPEN_EXISTING,
            HidInterop.FILE_FLAG_OVERLAPPED,
            nint.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            var inner = new IOException($"CreateFile failed for '{resolvedPath}'. Win32 error: {error}");
            throw error switch
            {
                ERROR_ACCESS_DENIED =>
                    new HidAccessDeniedException(
                        $"Access denied opening HID device '{devicePath}'. "
                        + "The device may be held exclusively by the OS driver (keyboards, mice) "
                        + "or the process lacks privileges.",
                        inner, devicePath),
                ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_DEVICE_NOT_CONNECTED =>
                    new HidDeviceNotFoundException(
                        $"HID device '{devicePath}' was not found. "
                        + "It may have been unplugged between enumeration and open.",
                        inner, devicePath),
                _ =>
                    new HidException(
                        $"Failed to open HID device '{devicePath}'. Win32 error: {error}",
                        inner, devicePath)
            };
        }

        nint preparsed = nint.Zero;
        try
        {
            if (!HidInterop.HidD_GetPreparsedData(handle, out preparsed))
            {
                var inner = new IOException($"HidD_GetPreparsedData failed for '{devicePath}'.");
                throw new HidException(
                    $"Failed to read HID capabilities for '{devicePath}'.", inner, devicePath);
            }

            var caps = new HidInterop.HidpCaps();
            if (HidInterop.HidP_GetCaps(preparsed, ref caps) != HidInterop.HIDP_STATUS_SUCCESS)
            {
                var inner = new IOException($"HidP_GetCaps failed for '{devicePath}'.");
                throw new HidException(
                    $"Failed to read HID capabilities for '{devicePath}'.", inner, devicePath);
            }

            var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096,
                isAsync: true);

            return new WindowsHidBackend(
                handle,
                stream,
                caps.UsagePage,
                caps.Usage,
                caps.InputReportByteLength,
                caps.OutputReportByteLength,
                caps.FeatureReportByteLength);
        }
        finally
        {
            if (preparsed != nint.Zero)
                HidInterop.HidD_FreePreparsedData(preparsed);
        }
    }

    public async Task<HidReport> ReadReportAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The OS always prepends the report ID byte; buffer must hold the full report.
        var buffer = new byte[_inputReportLength > 0 ? _inputReportLength : 65];
        int read;
        try
        {
            read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new HidTransferException(
                "HID read failed — the device may have been disconnected.", ex);
        }

        if (read == 0)
            throw new HidTransferException(
                "HID read returned 0 bytes — the device was disconnected.",
                new IOException("Zero-byte read on HID stream."));

        byte reportId = buffer[0];
        return new HidReport(reportId, buffer.AsMemory(1, read - 1));
    }

    public async Task WriteReportAsync(HidReport report, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int length = _outputReportLength > 0 ? _outputReportLength : report.Data.Length + 1;
        var buffer = new byte[length];
        buffer[0] = report.ReportId;
        report.Data.Span.CopyTo(buffer.AsSpan(1));

        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new HidTransferException(
                "HID write failed — the device may have been disconnected.", ex);
        }
    }

    // -----------------------------------------------------------------------
    // Feature reports (ADR-0048)
    //
    // HidD_GetFeature / HidD_SetFeature are inherently synchronous at the
    // OS level — they go through the HID stack's control-pipe path rather
    // than the data pipe FileStream wraps. We expose them via the async
    // interface for API consistency and to keep the caller's thread free
    // (a vendor-driver-mediated feature query can stall briefly), so the
    // sync call is wrapped in Task.Run.
    // -----------------------------------------------------------------------

    public Task<HidReport> ReadFeatureReportAsync(byte reportId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_featureReportLength <= 0)
            throw new HidException(
                "Device does not advertise any feature reports (FeatureReportByteLength == 0).");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Buffer holds full report including the leading report ID byte.
            // Caller writes the requested ID into buffer[0]; HidD_GetFeature
            // fills the rest with the device's response payload.
            var buffer = new byte[_featureReportLength];
            buffer[0] = reportId;

            if (!HidInterop.HidD_GetFeature(_handle, buffer, (uint)buffer.Length))
            {
                int error = Marshal.GetLastPInvokeError();
                var inner = new IOException(
                    $"HidD_GetFeature(reportId=0x{reportId:X2}) failed. Win32 error: {error}");
                throw new HidTransferException(
                    $"HID feature-report read failed for report 0x{reportId:X2}. " +
                    "The device may be locked by a vendor driver, may not implement " +
                    "this report ID, or may have been disconnected.",
                    inner);
            }

            // The device may echo back a different report ID than requested
            // if the report descriptor declares multiple IDs and the caller
            // asked for "any" (reportId == 0). Read it back from buffer[0].
            byte respondedId = buffer[0];
            return new HidReport(respondedId, buffer.AsMemory(1));
        }, ct);
    }

    public Task WriteFeatureReportAsync(HidReport report, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Buffer sized to the report-descriptor-declared length when
            // available; falls back to payload + ID byte for devices that
            // don't advertise the size (rare for compliant HID, plausible
            // for vendor-defined surfaces).
            int length = _featureReportLength > 0
                ? _featureReportLength
                : report.Data.Length + 1;
            var buffer = new byte[length];
            buffer[0] = report.ReportId;
            report.Data.Span.CopyTo(buffer.AsSpan(1));

            if (!HidInterop.HidD_SetFeature(_handle, buffer, (uint)buffer.Length))
            {
                int error = Marshal.GetLastPInvokeError();
                var inner = new IOException(
                    $"HidD_SetFeature(reportId=0x{report.ReportId:X2}) failed. Win32 error: {error}");
                throw new HidTransferException(
                    $"HID feature-report write failed for report 0x{report.ReportId:X2}. " +
                    "The device may be locked by a vendor driver, may have rejected " +
                    "the payload, or may have been disconnected.",
                    inner);
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _handle.Dispose();
    }

    /// <summary>
    /// Resolves a SetupAPI device-instance ID into its HID device-interface
    /// path so <c>CreateFile</c> can open it. Inputs that already look like
    /// an interface path (start with <c>\\?\</c> or <c>\\.\</c>) pass through
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// Uses <c>CM_Get_Device_Interface_List</c> on
    /// <see cref="HidInterop.GUID_DEVINTERFACE_HID"/>. If the instance ID has
    /// no HID interface (extremely unusual — would mean the device is
    /// registered but has no driver-published interface), returns the input
    /// path verbatim and lets <c>CreateFile</c> fail naturally with
    /// <c>ERROR_INVALID_NAME</c>; that surface error is more diagnostic for
    /// the caller than a wrapped exception would be.
    /// </remarks>
    private static string ResolveInterfacePath(string input)
    {
        // Already an interface path — pass through.
        if (input.StartsWith(@"\\?\", StringComparison.Ordinal)
            || input.StartsWith(@"\\.\", StringComparison.Ordinal))
            return input;

        var hidGuid = HidInterop.GUID_DEVINTERFACE_HID;

        // Size query first — the API needs a buffer big enough to hold
        // a multi-string of interface paths (each null-terminated, list
        // terminated by an extra null).
        int sizeResult = HidInterop.CM_Get_Device_Interface_List_Size(
            out uint lenChars,
            hidGuid,
            input,
            HidInterop.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

        if (sizeResult != HidInterop.CR_SUCCESS || lenChars <= 1)
            return input; // Nothing to resolve; let CreateFile produce the diagnostic.

        var buffer = new char[lenChars];
        int listResult = HidInterop.CM_Get_Device_Interface_List(
            hidGuid,
            input,
            buffer,
            lenChars,
            HidInterop.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

        if (listResult != HidInterop.CR_SUCCESS)
            return input;

        // Multi-string parsing — first null-terminated segment is the
        // path we want. HID devices generally expose exactly one
        // interface per (instance-id, GUID_DEVINTERFACE_HID) pair.
        int firstNull = Array.IndexOf(buffer, '\0');
        if (firstNull <= 0)
            return input;

        return new string(buffer, 0, firstNull);
    }
}
