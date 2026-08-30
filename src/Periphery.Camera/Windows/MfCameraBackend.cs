// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Periphery.Camera.Internal;

namespace Periphery.Camera.Windows;

/// <summary>
/// Windows Media Foundation implementation of <see cref="ICameraBackend"/>.
/// Uses IMFSourceReader for frame capture and IAMCameraControl/IAMVideoProcAmp
/// for hardware controls. Built on source-generated COM
/// (<see cref="GeneratedComInterfaceAttribute"/>) for AOT/trim compatibility.
/// </summary>
/// <remarks>
/// If you hit <c>InvalidCastException</c> after touching this file, the most
/// likely cause is a wrong IID in MfInterop. Verify against the canonical
/// SDK header (mfobjects.h / mfreadwrite.h via win32metadata) and re-run with
/// <see cref="MfInterop.ProbeQi"/>. See
/// <c>docs/patterns/source-generated-com-interop.md</c> Hazard A.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MfCameraBackend : ICameraBackend
{
    private readonly DeviceInfo _deviceInfo;
    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private IAMCameraControl? _cameraControl;
    private IAMVideoProcAmp? _videoProcAmp;
    private CameraConfiguration? _configuration;
    private volatile bool _isCapturing;
    private bool _disposed;

    // Reused per-frame capture buffer (LOH-churn fix). MF previously allocated a fresh ~MB array
    // per frame (1280x720 BGRA32 = 3.7 MB -> ~100 MB/s of LOH at 30fps), driving continuous gen2.
    // ExtractFrame runs single-threaded on the producer's LongRunning thread and the pool copies
    // RawCameraFrame.Data out before the next read, so one reused buffer can back every frame -- the
    // same transient-buffer contract V4l2 already relies on for its mmap mapping.
    private byte[] _frameBuffer = [];

    internal MfCameraBackend(DeviceInfo deviceInfo)
    {
        _deviceInfo = deviceInfo;
    }

    public string NativeEndpointId { get; private set; } = string.Empty;

    // ═══════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    public Task OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        MfRuntime.EnsureStarted();

        try
        {
            _source = ActivateSource(_deviceInfo.Id);

            // QI for camera-control interfaces. With source-generated wrappers
            // the cast operator routes through IDynamicInterfaceCastable, which
            // performs QueryInterface on the underlying COM pointer — null when
            // the device doesn't expose that interface.
            _cameraControl = _source as IAMCameraControl;
            _videoProcAmp = _source as IAMVideoProcAmp;

            IMFAttributes? readerAttrs = CreateReaderAttributes();
            try
            {
                ThrowForHr(
                    MfInterop.MFCreateSourceReaderFromMediaSource(_source, readerAttrs, out _reader),
                    "Failed to create source reader");
            }
            finally
            {
                MfInterop.Release(ref readerAttrs);
            }

            ThrowForHr(
                _reader!.SetStreamSelection(MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true),
                "Failed to select video stream");
        }
        catch
        {
            Cleanup();
            MfRuntime.Release();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();
        return Task.Run<IReadOnlyList<CameraFormat>>(() => EnumerateFormats(), ct);
    }

    public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();
        return Task.Run<IReadOnlyList<CameraControlInfo>>(() => EnumerateControls(), ct);
    }

    public Task<CameraControlState?> GetControlAsync(CameraControlKind control, CancellationToken ct)
    {
        ThrowIfNotOpen();

        return Task.Run<CameraControlState?>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!MfFormatMap.TryGetPropertyId(control, out int propertyId, out bool isCameraControl))
                return null;

            // The device may not expose the INTERFACE the property lives behind
            // at all — plenty of fixed-lens UVC cameras offer IAMVideoProcAmp and
            // no IAMCameraControl. EnumerateControls already skips those; without
            // the same check here, RequireCameraControl throws where this method
            // has promised to return null, and it throws synchronously out of a
            // Task-returning method at that.
            var cameraControl = isCameraControl ? _cameraControl : null;
            var videoProcAmp = isCameraControl ? null : _videoProcAmp;
            if (isCameraControl ? cameraControl is null : videoProcAmp is null)
                return null;

            int hr = isCameraControl
                ? cameraControl!.Get(propertyId, out int value, out int flags)
                : videoProcAmp!.Get(propertyId, out value, out flags);

            // "This driver does not implement that property" is an answer, and
            // the caller should receive it without catching. Anything else is
            // not: E_ACCESSDENIED and the device-invalidated HRESULTs are exactly
            // what a USB blip produces, and swallowing them would report a
            // vanished camera as one that simply has no exposure control — an
            // indefinite run of nulls where the rest of the backend raises
            // CameraDeviceLostException.
            if (hr == MfInterop.E_PROP_ID_UNSUPPORTED)
                return null;
            ThrowForHr(hr, $"Failed to read control {control}");

            var mode = (flags & MfInterop.MF_CAMERA_FLAGS_AUTO) != 0
                ? CameraControlMode.Automatic
                : (flags & MfInterop.MF_CAMERA_FLAGS_MANUAL) != 0
                    ? CameraControlMode.Manual
                    : CameraControlMode.Unknown;

            return new CameraControlState(control, value, mode);
        }, ct);
    }

    public Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct)
    {
        ThrowIfNotOpen();

        if (!MfFormatMap.TryGetPropertyId(control, out int propertyId, out bool isCameraControl))
            throw new CameraException($"Control {control} is not mapped to a Media Foundation property.", _deviceInfo.Id);

        int intValue = (int)Math.Round(value);
        int hr = isCameraControl
            ? RequireCameraControl().Set(propertyId, intValue, MfInterop.MF_CAMERA_FLAGS_MANUAL)
            : RequireVideoProcAmp().Set(propertyId, intValue, MfInterop.MF_CAMERA_FLAGS_MANUAL);

        ThrowForHr(hr, $"Failed to set control {control}");
        return Task.CompletedTask;
    }

    public Task ResetControlAsync(CameraControlKind control, CancellationToken ct)
    {
        ThrowIfNotOpen();

        if (!MfFormatMap.TryGetPropertyId(control, out int propertyId, out bool isCameraControl))
            throw new CameraException($"Control {control} is not mapped.", _deviceInfo.Id);

        int hr;
        int defaultValue;
        if (isCameraControl)
        {
            var cc = RequireCameraControl();
            hr = cc.GetRange(propertyId, out _, out _, out _, out defaultValue, out _);
            ThrowForHr(hr, $"Failed to get default for {control}");
            hr = cc.Set(propertyId, defaultValue, MfInterop.MF_CAMERA_FLAGS_AUTO);
        }
        else
        {
            var amp = RequireVideoProcAmp();
            hr = amp.GetRange(propertyId, out _, out _, out _, out defaultValue, out _);
            ThrowForHr(hr, $"Failed to get default for {control}");
            hr = amp.Set(propertyId, defaultValue, MfInterop.MF_CAMERA_FLAGS_AUTO);
        }

        ThrowForHr(hr, $"Failed to reset control {control}");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Configuration
    // ═══════════════════════════════════════════════════════════════════

    public Task ConfigureAsync(CameraConfiguration configuration, CancellationToken ct)
    {
        ThrowIfNotOpen();
        ct.ThrowIfCancellationRequested();

        var format = configuration.Format;

        if (!MfFormatMap.TryMapFormat(format.PixelFormat, out Guid subtype))
            throw new CameraConfigurationException(
                $"Pixel format {format.PixelFormat} has no Media Foundation equivalent.", _deviceInfo.Id);

        var mediaType = FindMatchingNativeType(format, subtype)
            ?? throw new CameraConfigurationException(
                $"Format {format.Width}x{format.Height} {format.PixelFormat} is not supported by this camera.",
                _deviceInfo.Id);

        try
        {
            ThrowForHr(
                _reader!.SetCurrentMediaType(MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, nint.Zero, mediaType),
                "Failed to set media type");
        }
        finally
        {
            MfInterop.Release(ref mediaType);
        }

        _configuration = configuration;
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Capture
    // ═══════════════════════════════════════════════════════════════════

    public Task StartCaptureAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();
        if (_configuration is null)
            throw new InvalidOperationException("Device not configured.");
        _isCapturing = true;
        return Task.CompletedTask;
    }

    public Task<RawCameraFrame> ReadRawFrameAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();
        if (!_isCapturing)
            throw new InvalidOperationException("Capture not started.");

        // Run synchronously on the caller's thread instead of hopping to the
        // thread pool. IMFSourceReader is documented as single-threaded —
        // "all source reader API calls must occur from a single thread" —
        // and a fresh ThreadPool task per ReadSample violates that, leading
        // to silent stalls after ~20 frames on every camera tested. The
        // CameraSession producer task is LongRunning so it has a dedicated
        // thread for this synchronous call.
        try { return Task.FromResult(ReadSampleCore(ct)); }
        catch (Exception ex) { return Task.FromException<RawCameraFrame>(ex); }
    }

    public Task StopCaptureAsync()
    {
        _isCapturing = false;
        _reader?.Flush(MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Core frame read — runs on thread pool
    // ═══════════════════════════════════════════════════════════════════

    private RawCameraFrame ReadSampleCore(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int hr = _reader!.ReadSample(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                0,
                out _,
                out uint streamFlags,
                out long timestamp100ns,
                out nint samplePtr);

            try
            {
                if (hr == MfInterop.MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
                    || hr == MfInterop.MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED)
                {
                    throw new CameraDeviceLostException(
                        "Camera device was disconnected or preempted.", _deviceInfo.Id);
                }

                ThrowForHr(hr, "ReadSample failed");

                if ((streamFlags & MfInterop.MF_SOURCE_READERF_ERROR) != 0)
                    throw new CameraDeviceLostException(
                        "Source reader reported an error.", _deviceInfo.Id);

                if ((streamFlags & MfInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    throw new CameraDeviceLostException(
                        "Camera stream ended unexpectedly.", _deviceInfo.Id);

                // Stream tick / no data this iteration: loop and try again.
                if (samplePtr == 0 || (streamFlags & MfInterop.MF_SOURCE_READERF_STREAMTICK) != 0)
                    continue;

                // Wrap with UniqueInstance to get a deterministic-disposal
                // ComObject. NOTE: GetOrCreateObjectForComInstance does NOT
                // take ownership of the input ref (it does its own QI for
                // IUnknown internally and the wrapper owns *that* ref).
                // The original samplePtr ref from ReadSample is ours to
                // release. Without doing so, MF's internal sample pool
                // exhausts after ~20 frames and ReadSample stalls forever.
                IMFSample sample = (IMFSample)MfInterop.Wrappers
                    .GetOrCreateObjectForComInstance(samplePtr, CreateObjectFlags.UniqueInstance);
                Marshal.Release(samplePtr);
                samplePtr = 0;
                try
                {
                    return ExtractFrame(sample, timestamp100ns);
                }
                finally
                {
                    MfInterop.Release(ref sample!);
                }
            }
            finally
            {
                // If we bailed before wrapping (error / stream tick), release
                // the raw ref directly so MF can recycle the sample.
                if (samplePtr != 0)
                {
                    Marshal.Release(samplePtr);
                    samplePtr = 0;
                }
            }
        }
    }

    // Returns the reused per-frame buffer, growing it only when a larger frame arrives. Safe to
    // share across frames: reads are single-threaded and the pool copies Data out before the next
    // read (RawCameraFrame's documented "valid until next ReadRawFrameAsync" contract).
    private byte[] EnsureFrameBuffer(int size)
    {
        if (_frameBuffer.Length < size)
            _frameBuffer = new byte[size];
        return _frameBuffer;
    }

    private RawCameraFrame ExtractFrame(IMFSample sample, long timestamp100ns)
    {
        ThrowForHr(sample.ConvertToContiguousBuffer(out nint bufferPtr),
            "ConvertToContiguousBuffer failed");

        IMFMediaBuffer? buffer = null;
        try
        {
            // Same ownership semantics as IMFSample wrapping above:
            // GetOrCreateObjectForComInstance does not take ownership of
            // the input ref. We release it explicitly after wrapping.
            buffer = (IMFMediaBuffer)MfInterop.Wrappers
                .GetOrCreateObjectForComInstance(bufferPtr, CreateObjectFlags.UniqueInstance);
            Marshal.Release(bufferPtr);
            bufferPtr = 0;

            var format = _configuration!.Format;
            int width = format.Width;
            int height = format.Height;

            byte[] data;
            int length;
            int stride;
            bool bottomUp = false;

            // Prefer IMF2DBuffer for stride-aware access. The cast routes
            // through IDynamicInterfaceCastable / QueryInterface — null when
            // the buffer doesn't expose the 2D interface.
            if (buffer is IMF2DBuffer buffer2D)
            {
                ThrowForHr(buffer2D.Lock2D(out nint scanline0, out stride), "Lock2D failed");
                try
                {
                    int absStride = Math.Abs(stride);
                    int frameSize = CameraFrameLayout.FrameSize(format.PixelFormat, width, height, absStride);
                    length = frameSize;
                    data = EnsureFrameBuffer(frameSize);

                    // Negative stride = bottom-up: scanline0 still points at the
                    // first image row, but that row sits at the HIGHEST address
                    // and each following one is a stride lower, so the surface
                    // itself begins (height - 1) rows below scanline0. Copying
                    // from there lifts the whole buffer in storage order —
                    // every plane, not just luma — and the pool flips it while
                    // it de-pads (ADR-0081 D8).
                    //
                    // This used to be a row loop here. It copied exactly
                    // `height` rows, which is the whole frame for RGB (where MF
                    // reports a negative stride) and two thirds of a 4:2:0 one,
                    // so a bottom-up NV12 frame kept the previous frame's chroma
                    // out of the reused buffer.
                    nint origin = stride > 0
                        ? scanline0
                        : scanline0 - ((nint)(height - 1) * absStride);
                    Marshal.Copy(origin, data, 0, frameSize);
                    bottomUp = stride < 0;
                }
                finally
                {
                    buffer2D.Unlock2D();
                }
            }
            else
            {
                ThrowForHr(buffer.Lock(out nint scanline0, out _, out int currentLength), "Lock failed");
                try
                {
                    length = currentLength;
                    data = EnsureFrameBuffer(currentLength);
                    Marshal.Copy(scanline0, data, 0, currentLength);
                    stride = CameraFrameLayout.BytesPerRow(format.PixelFormat, width);
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            // Stride may be negative when MF reports a bottom-up image. The
            // descriptors describe the *managed* buffer, whose rows are the
            // platform's own — same pitch, same order — so the magnitude is the
            // stride and BottomUp carries the direction to the pool.
            int rowStride = Math.Abs(stride);
            return new RawCameraFrame
            {
                Data = data.AsMemory(0, length),
                Width = width,
                Height = height,
                PixelFormat = format.PixelFormat,
                Timestamp = TimeSpan.FromTicks(timestamp100ns),
                PlaneCount = CameraFrameLayout.PlaneCount(format.PixelFormat),
                Planes = PlaneLayout.DescribePlanes(format.PixelFormat, width, height, rowStride),
                BottomUp = bottomUp,
            };
        }
        finally
        {
            MfInterop.Release(ref buffer);
            if (bufferPtr != 0) Marshal.Release(bufferPtr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Format enumeration
    // ═══════════════════════════════════════════════════════════════════

    private List<CameraFormat> EnumerateFormats()
    {
        var formats = new List<CameraFormat>();

        for (uint i = 0; ; i++)
        {
            int hr = _reader!.GetNativeMediaType(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, i, out IMFMediaType mediaType);

            if (hr == MfInterop.MF_E_NO_MORE_TYPES) break;
            ThrowForHr(hr, "GetNativeMediaType failed");

            try
            {
                if (TryParseFormat(mediaType, out var format))
                    formats.Add(format);
            }
            finally
            {
                MfInterop.Release(ref mediaType!);
            }
        }

        return formats;
    }

    private static bool TryParseFormat(IMFMediaType mediaType, out CameraFormat format)
    {
        format = default!;

        if (mediaType.GetGUID(MfInterop.MF_MT_SUBTYPE, out Guid subtype) < 0)
            return false;

        if (!MfFormatMap.TryMapSubtype(subtype, out var pixelFormat, out var transport))
            return false;

        if (mediaType.GetUINT64(MfInterop.MF_MT_FRAME_SIZE, out ulong frameSize) < 0)
            return false;
        MfInterop.Unpack2xUInt32(frameSize, out uint width, out uint height);

        Rational minFps = new(1);
        Rational maxFps = new(30);

        if (mediaType.GetUINT64(MfInterop.MF_MT_FRAME_RATE, out ulong frameRate) >= 0)
        {
            MfInterop.Unpack2xUInt32(frameRate, out uint num, out uint denom);
            if (denom > 0) maxFps = new Rational((int)num, (int)denom);
        }

        if (mediaType.GetUINT64(MfInterop.MF_MT_FRAME_RATE_RANGE_MIN, out ulong minRate) >= 0)
        {
            MfInterop.Unpack2xUInt32(minRate, out uint num, out uint denom);
            if (denom > 0) minFps = new Rational((int)num, (int)denom);
        }

        if (mediaType.GetUINT64(MfInterop.MF_MT_FRAME_RATE_RANGE_MAX, out ulong maxRate) >= 0)
        {
            MfInterop.Unpack2xUInt32(maxRate, out uint num, out uint denom);
            if (denom > 0) maxFps = new Rational((int)num, (int)denom);
        }

        format = new CameraFormat((int)width, (int)height, pixelFormat, minFps, maxFps, transport);
        return true;
    }

    private IMFMediaType? FindMatchingNativeType(CameraFormat target, Guid subtype)
    {
        for (uint i = 0; ; i++)
        {
            int hr = _reader!.GetNativeMediaType(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, i, out IMFMediaType mediaType);

            if (hr == MfInterop.MF_E_NO_MORE_TYPES) return null;
            ThrowForHr(hr, "GetNativeMediaType failed during format search");

            bool matched = false;
            try
            {
                if (mediaType.GetGUID(MfInterop.MF_MT_SUBTYPE, out Guid nativeSubtype) < 0
                    || nativeSubtype != subtype)
                {
                    continue;
                }

                if (mediaType.GetUINT64(MfInterop.MF_MT_FRAME_SIZE, out ulong size) < 0)
                    continue;

                MfInterop.Unpack2xUInt32(size, out uint w, out uint h);
                if ((int)w == target.Width && (int)h == target.Height)
                {
                    matched = true;
                    return mediaType;
                }
            }
            finally
            {
                if (!matched)
                {
                    var toRelease = mediaType;
                    MfInterop.Release(ref toRelease!);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Control enumeration
    // ═══════════════════════════════════════════════════════════════════

    private List<CameraControlInfo> EnumerateControls()
    {
        var controls = new List<CameraControlInfo>();

        foreach (var (propertyId, kind, isCameraControl) in MfFormatMap.AllKnownControls)
        {
            int hr;
            int min, max, step, defaultValue, capsFlags;

            if (isCameraControl)
            {
                if (_cameraControl is null) continue;
                hr = _cameraControl.GetRange(propertyId, out min, out max, out step, out defaultValue, out capsFlags);
            }
            else
            {
                if (_videoProcAmp is null) continue;
                hr = _videoProcAmp.GetRange(propertyId, out min, out max, out step, out defaultValue, out capsFlags);
            }

            if (hr < 0) continue;

            bool isAuto = (capsFlags & MfInterop.MF_CAMERA_FLAGS_AUTO) != 0;
            bool isReadOnly = (capsFlags & MfInterop.MF_CAMERA_FLAGS_MANUAL) == 0 && !isAuto;

            controls.Add(new CameraControlInfo(
                kind, kind.ToString(), min, max, step, defaultValue, isAuto, isReadOnly));
        }

        return controls;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Device activation
    // ═══════════════════════════════════════════════════════════════════

    private IMFMediaSource ActivateSource(string deviceId)
    {
        ThrowForHr(MfInterop.MFCreateAttributes(out IMFAttributes attrs, 1),
            "MFCreateAttributes failed", deviceId);

        try
        {
            ThrowForHr(
                attrs.SetGUID(
                    MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                    MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID),
                "SetGUID(SOURCE_TYPE) failed", deviceId);

            int hr = MfInterop.MFEnumDeviceSources(attrs, out nint arrayPtr, out uint count);
            if (hr < 0)
            {
                throw new CameraDeviceNotFoundException(
                    $"Failed to enumerate camera devices (0x{hr:X8}).",
                    Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException(),
                    deviceId);
            }

            if (arrayPtr == 0 || count == 0)
            {
                if (arrayPtr != 0) Marshal.FreeCoTaskMem(arrayPtr);
                throw new CameraDeviceNotFoundException("No camera devices found.", deviceId);
            }

            // Walk the CoTaskMem array of IMFActivate*. Each element holds a
            // +1 ref from MF; ComWrappers takes ownership of that ref via the
            // returned wrapper, so we only free the array buffer afterwards.
            var activates = new IMFActivate[count];
            try
            {
                for (int i = 0; i < (int)count; i++)
                {
                    nint ptr = Marshal.ReadIntPtr(arrayPtr, i * nint.Size);
                    activates[i] = (IMFActivate)MfInterop.Wrappers.GetOrCreateObjectForComInstance(
                        ptr, CreateObjectFlags.UniqueInstance);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(arrayPtr);
            }

            try
            {
                IMFActivate? match = FindMatchingActivate(activates, deviceId)
                    ?? throw new CameraDeviceNotFoundException(
                        $"Camera device '{deviceId}' not found in MF device list.", deviceId);

                if (MfInterop.GetAllocatedString(
                        match, MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                        out string symLink) >= 0)
                {
                    NativeEndpointId = symLink;
                }

                hr = match.ActivateObject(MfInterop.IID_IMFMediaSource, out IMFMediaSource source);
                if (hr == MfInterop.E_ACCESSDENIED)
                {
                    throw new CameraAccessDeniedException(
                        $"Access denied activating camera '{deviceId}'. " +
                        "Camera privacy settings may be blocking access.",
                        Marshal.GetExceptionForHR(hr) ?? new UnauthorizedAccessException(),
                        deviceId);
                }
                ThrowForHr(hr, "ActivateObject failed", deviceId);
                return source;
            }
            finally
            {
                for (int i = 0; i < activates.Length; i++)
                {
                    var a = activates[i];
                    MfInterop.Release(ref a);
                }
            }
        }
        finally
        {
            MfInterop.Release(ref attrs!);
        }
    }

    private static IMFActivate? FindMatchingActivate(IMFActivate[] activates, string deviceId)
    {
        // First pass: match by symbolic link (contains the SetupAPI device id).
        foreach (var activate in activates)
        {
            if (MfInterop.GetAllocatedString(
                    activate, MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                    out string symLink) >= 0
                && ContainsDeviceId(symLink, deviceId))
            {
                return activate;
            }
        }

        // Fallback: match by friendly name (when callers pass a human id).
        foreach (var activate in activates)
        {
            if (MfInterop.GetAllocatedString(
                    activate, MfInterop.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                    out string friendlyName) >= 0
                && string.Equals(friendlyName, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return activate;
            }
        }

        return null;
    }

    private static bool ContainsDeviceId(string symbolicLink, string deviceId)
    {
        // SetupAPI device IDs (e.g. "USB\VID_046D&PID_0825\12345") appear inside
        // the MF symbolic link with '#' as the separator instead of '\'.
        var normalized = symbolicLink.Replace('#', '\\');
        return normalized.Contains(deviceId, StringComparison.OrdinalIgnoreCase)
            || symbolicLink.Contains(deviceId, StringComparison.OrdinalIgnoreCase);
    }

    private static IMFAttributes? CreateReaderAttributes()
    {
        if (MfInterop.MFCreateAttributes(out IMFAttributes attrs, 1) < 0) return null;
        // Pass camera-native bytes straight through — keeps MJPEG frames as
        // JPEG payloads and avoids hidden conversions that surprise consumers.
        attrs.SetUINT32(MfInterop.MF_READWRITE_DISABLE_CONVERTERS, 1);
        return attrs;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private void ThrowIfNotOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reader is null)
            throw new InvalidOperationException("Backend is not open.");
    }

    private IAMCameraControl RequireCameraControl() =>
        _cameraControl ?? throw new CameraException(
            "Camera control interface not available on this device.", _deviceInfo.Id);

    private IAMVideoProcAmp RequireVideoProcAmp() =>
        _videoProcAmp ?? throw new CameraException(
            "Video proc amp interface not available on this device.", _deviceInfo.Id);

    private void ThrowForHr(int hr, string context, string? deviceId = null)
    {
        if (hr >= 0) return;

        deviceId ??= _deviceInfo.Id;
        var inner = Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException(context);

        throw hr switch
        {
            MfInterop.E_ACCESSDENIED => new CameraAccessDeniedException(
                $"{context}: access denied.", inner, deviceId),

            MfInterop.MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
            or MfInterop.MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED => new CameraDeviceLostException(
                $"{context}: device lost.", inner, deviceId),

            MfInterop.E_NOT_FOUND => new CameraDeviceNotFoundException(
                $"{context}: not found.", inner, deviceId),

            _ => new CameraException($"{context} (HRESULT 0x{hr:X8})", inner, deviceId),
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _isCapturing = false;

        // Run COM cleanup on a background thread with a hard timeout. Wedged
        // USB camera drivers can block ANY MF call (Flush, Shutdown, even
        // Release on the COM ref) indefinitely; we've seen NexiGo MJPEG
        // 1280x720 30fps freeze after ~20 frames where Flush itself never
        // returns. Disposal must complete in bounded time so the process
        // can exit; any abandoned work continues on a background thread,
        // which is fine because thread-pool threads don't block process
        // exit and Windows reclaims the COM resources at process end.
        var cleanupTask = Task.Run(Cleanup);
        var winner = await Task.WhenAny(cleanupTask, Task.Delay(TimeSpan.FromSeconds(3)))
            .ConfigureAwait(false);
        if (winner != cleanupTask)
        {
            Console.Error.WriteLine(
                "WARNING: MF cleanup did not complete within 3s; abandoning. " +
                "If this happens often the camera driver may be wedged — try replugging.");
        }

        MfRuntime.Release();
    }

    private void Cleanup()
    {
        // QI'd interfaces share lifetime with the source — null them first so
        // the underlying ref count releases when the source wrapper is freed.
        _cameraControl = null;
        _videoProcAmp = null;

        // Order matters: Shutdown the source FIRST. That cancels any pending
        // ReadSample calls on the producer thread and is the most reliable
        // way to interrupt a wedged driver. After Shutdown, Flush + Release
        // on the reader are best-effort; if they also wedge, the outer
        // bounded wait in DisposeAsync abandons us.
        if (_source is not null)
        {
            try { _source.Shutdown(); } catch { /* best effort */ }
        }

        if (_reader is not null)
        {
            try { _reader.Flush(MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM); }
            catch { /* best effort */ }
            MfInterop.Release(ref _reader);
        }

        if (_source is not null)
            MfInterop.Release(ref _source);
    }
}

/// <summary>
/// Ref-counted MFStartup/MFShutdown manager. Multiple backends can be open
/// concurrently; MF is initialized on first open and shut down when the
/// last backend disposes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MfRuntime
{
    private static int s_refCount;
    private static readonly object s_lock = new();

    internal static void EnsureStarted()
    {
        lock (s_lock)
        {
            if (s_refCount == 0)
            {
                int hr = MfInterop.MFStartup(MfInterop.MF_VERSION, MfInterop.MFSTARTUP_NOSOCKET);
                if (hr < 0)
                {
                    var inner = Marshal.GetExceptionForHR(hr)
                        ?? new InvalidOperationException("MFStartup failed");
                    throw new CameraException($"MFStartup failed (0x{hr:X8})", inner);
                }
            }
            s_refCount++;
        }
    }

    internal static void Release()
    {
        lock (s_lock)
        {
            if (s_refCount > 0 && --s_refCount == 0)
            {
                MfInterop.MFShutdown();
            }
        }
    }
}
