// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Camera.Internal;

namespace Periphery.Camera.Testing;

/// <summary>
/// A hardware-free <c>ICameraBackend</c> for unit-testing code that wraps
/// <see cref="CameraSession"/> / <see cref="CameraDevice"/>. Generates synthetic
/// frames and can simulate the failure modes real drivers exhibit — a refused
/// open, a mid-stream read fault, a wedged (never-returning) read, and per-frame
/// pacing — so a consumer's capture pump, reconnect policy, and teardown can be
/// exercised deterministically with a <c>FakeTimeProvider</c> and no camera.
/// </summary>
/// <remarks>
/// <para>
/// This is the supported, public form of Periphery's own internal camera test
/// fake (periphery ADR-0065). The platform I/O contract it implements
/// (<c>Periphery.Camera.Internal.ICameraBackend</c>) stays internal and free to
/// evolve — this type implements it <b>explicitly</b>, so the only public surface
/// is the configuration + observation API below.
/// </para>
/// <para>
/// Install it on the construction path via <see cref="CameraTestScope"/> (so
/// <c>CameraSession.For(deviceInfo).OpenAsync()</c> resolves to this fake), or
/// hand it directly to <see cref="CameraTestHarness"/> for code that accepts an
/// already-open session.
/// </para>
/// <para>
/// <b>Frame content.</b> By default every frame is one constant byte, which is
/// enough for lifecycle and cadence tests and useless for pixel ones. Set
/// <see cref="FrameFactory"/> (see <see cref="CameraFramePatterns"/>) to write
/// known bytes at known offsets, and <see cref="OverrideStride"/> to pad the
/// rows. Geometry — size, plane count, plane offsets — always follows
/// <see cref="CameraFrameLayout"/>, so a 4:2:0 frame from the fake is shaped
/// like one from a real backend.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> Set the hooks before the code under test starts
/// capturing. Capture mutates backend state on the producer thread — the
/// interlocked frame counter, and <see cref="FaultOnNextRead"/> is cleared after
/// it fires — so racing that by writing hooks from another thread mid-capture is
/// not supported.
/// </para>
/// <para>
/// <b>Lifecycle fidelity.</b> Like the real backends, the fake throws
/// <see cref="ObjectDisposedException"/> for use after dispose — including a
/// second <c>OpenAsync</c> — and <see cref="InvalidOperationException"/> for
/// capability/capture calls before the device is opened, so consumer bugs the
/// seam exists to catch aren't masked. An instance models <em>one</em> device
/// lifecycle, matching real code, where the backend factory mints a fresh
/// backend per open. Paths that open twice (notably the
/// <see cref="CameraSessionBuilder"/>'s snapshot pass followed by the capture
/// open) therefore need the per-open
/// <see cref="CameraTestScope.Install(Func{DeviceInfo, InMemoryCameraBackend})"/>
/// overload, not a single shared instance.
/// </para>
/// </remarks>
public sealed class InMemoryCameraBackend : ICameraBackend
{
    private readonly string _nativeEndpointId;
    private readonly List<CameraFormat> _formats;
    private readonly List<CameraControlInfo> _controls;
    private readonly Dictionary<CameraControlKind, double> _controlValues = new();
    private readonly Dictionary<CameraControlKind, CameraControlMode> _controlModes = new();
    private readonly HashSet<CameraControlKind> _controlsRefusingRead = [];
    private readonly TaskCompletionSource _readHangReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CameraConfiguration? _configuration;
    private int _frameCounter;
    private bool _isCapturing;
    private bool _isOpen;
    private bool _disposed;

    /// <summary>
    /// Create a fake backend.
    /// </summary>
    /// <param name="nativeEndpointId">The value surfaced as
    /// <c>CameraDevice.NativeEndpointId</c> / <c>CameraSession.Device.NativeEndpointId</c>.</param>
    /// <param name="formats">Advertised formats. Defaults to
    /// <see cref="CameraTestFormats.Default"/> (a spread of YUY2 / MJPEG / NV12
    /// resolutions) when null.</param>
    /// <param name="controls">Advertised controls. Defaults to
    /// <see cref="CameraTestFormats.DefaultControls"/> when null.</param>
    public InMemoryCameraBackend(
        string nativeEndpointId = "test://camera0",
        IEnumerable<CameraFormat>? formats = null,
        IEnumerable<CameraControlInfo>? controls = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeEndpointId);
        _nativeEndpointId = nativeEndpointId;
        _formats = formats?.ToList() ?? CameraTestFormats.Default.ToList();
        _controls = controls?.ToList() ?? CameraTestFormats.DefaultControls.ToList();
    }

    // ── Failure-mode hooks ─────────────────────────────────────────────

    /// <summary>When set, the next <c>ReadRawFrameAsync</c> throws this and then
    /// clears itself — models a single mid-stream driver fault (e.g. a device
    /// unplugged during capture).</summary>
    public Exception? FaultOnNextRead { get; set; }

    /// <summary>When set, opening the device throws this — models a driver that
    /// refuses to (re)open.</summary>
    public Exception? FaultOnOpen { get; set; }

    /// <summary>When true, <c>ReadRawFrameAsync</c> parks forever (until its token
    /// is cancelled) instead of producing a frame — models a wedged stream that
    /// stops delivering. Combined with a <c>FakeTimeProvider</c> this drives the
    /// session's frame-timeout deterministically.</summary>
    public bool HangOnRead { get; set; }

    /// <summary>Produce this many frames, then park forever on subsequent reads.
    /// Lets a test pin an exact frame count without racing the producer loop.
    /// Defaults to <see cref="int.MaxValue"/> (unbounded).</summary>
    public int MaxFrames { get; set; } = int.MaxValue;

    /// <summary>Optional per-frame delay, over the ambient clock, to loosely model
    /// real frame cadence. Prefer a <c>FakeTimeProvider</c> on the session for
    /// deterministic timing; this uses real time and is best left at zero in
    /// unit tests.</summary>
    public TimeSpan FrameDelay { get; set; }

    /// <summary>When set, generated frames use this pixel format instead of the
    /// configured one — lets a test assert a consumer's pixel-format handling
    /// independent of format selection.</summary>
    public CameraPixelFormat? OverridePixelFormat { get; set; }

    // ── Frame-content hooks ────────────────────────────────────────────

    /// <summary>
    /// When set, supplies the frame's bytes. Receives a
    /// <see cref="CameraFrameSpec"/> carrying the format, dimensions, stride, and
    /// frame index, and must return exactly
    /// <see cref="CameraFrameSpec.FrameSize"/> bytes.
    /// <see cref="CameraFramePatterns"/> ships the usual few; hand-write one when
    /// a test needs specific pixels.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CameraFramePatterns.FrameIndexConstant"/>, which is
    /// what the fake has always produced. MJPEG is the one format whose returned
    /// length is not checked exactly — its <c>FrameSize</c> is a worst-case bound
    /// rather than an exact size, so a real JPEG blob is legitimately shorter.
    /// </remarks>
    public Func<CameraFrameSpec, byte[]>? FrameFactory { get; set; }

    /// <summary>
    /// When set, forces the luma/packed row stride to this many bytes instead of
    /// the format's natural <see cref="CameraFrameLayout.BytesPerRow"/> — models a
    /// driver that aligns rows to a hardware boundary. The buffer grows to match
    /// and the plane descriptors carry the padded stride, so a consumer that walks
    /// rows by width instead of stride skews here exactly as it does on hardware
    /// that pads (#320).
    /// </summary>
    /// <remarks>
    /// Must be at least the natural stride; a narrower value throws when the
    /// frame is generated. Ignored for MJPEG, which has no rows.
    /// </remarks>
    public int? OverrideStride { get; set; }

    // ── Observation hooks ──────────────────────────────────────────────

    // These three are cross-thread observation points — capture flips them from
    // the producer thread while the test thread polls — so they read/write
    // through Volatile, like FrameCounter. A plain field read is hoistable out
    // of a spin loop, which would hang a consumer's `while (!backend.IsCapturing)`.

    /// <summary>Whether the device is currently open.</summary>
    public bool IsOpen => Volatile.Read(ref _isOpen);

    /// <summary>Whether capture is currently running.</summary>
    public bool IsCapturing => Volatile.Read(ref _isCapturing);

    /// <summary>Whether the backend has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed);

    /// <summary>Frames produced so far since construction.</summary>
    public int FrameCounter => Volatile.Read(ref _frameCounter);

    /// <summary>The configuration the session applied, or null before configure.</summary>
    public CameraConfiguration? AppliedConfiguration => _configuration;

    /// <summary>
    /// Completes the moment a read parks on a hang (via <see cref="HangOnRead"/>
    /// or the <see cref="MaxFrames"/> cap). Await it to know the producer is
    /// genuinely blocked — and therefore the consumer has reached its next-frame
    /// wait — before advancing a <c>FakeTimeProvider</c>, instead of guessing
    /// with a sleep.
    /// </summary>
    public Task ReadHangReached => _readHangReached.Task;

    /// <summary>The last value written to <paramref name="kind"/> via the session's
    /// control API, or null if never set.</summary>
    public double? GetControlValue(CameraControlKind kind) =>
        _controlValues.TryGetValue(kind, out var v) ? v : null;

    // ── ICameraBackend (explicit — the internal I/O contract stays hidden) ──

    string ICameraBackend.NativeEndpointId => _nativeEndpointId;

    Task ICameraBackend.OpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (FaultOnOpen is { } ex)
            throw ex;
        Volatile.Write(ref _isOpen, true);
        return Task.CompletedTask;
    }

    Task<IReadOnlyList<CameraFormat>> ICameraBackend.GetFormatsAsync(CancellationToken ct)
    {
        EnsureOpen();
        return Task.FromResult<IReadOnlyList<CameraFormat>>(_formats.AsReadOnly());
    }

    Task<IReadOnlyList<CameraControlInfo>> ICameraBackend.GetControlsAsync(CancellationToken ct)
    {
        EnsureOpen();
        return Task.FromResult<IReadOnlyList<CameraControlInfo>>(_controls.AsReadOnly());
    }

    /// <summary>
    /// Put a control into a specific state, including one the fake would not
    /// otherwise reach — notably <see cref="CameraControlMode.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// Without this the fake models an idealised camera: mode is derived from
    /// whether the control advertises an auto mode, so <c>Unknown</c> is
    /// unreachable and a consumer whose restore logic mishandles it gets a green
    /// suite. Real drivers return neither flag often enough that the member
    /// exists at all.
    /// </remarks>
    public void SetControlState(CameraControlKind control, double value, CameraControlMode mode)
    {
        _controlValues[control] = value;
        _controlModes[control] = mode;
    }

    /// <summary>
    /// Make a control refuse to report itself, as a driver that answers
    /// enumeration but not a read does.
    /// </summary>
    public void RefuseControlRead(CameraControlKind control) => _controlsRefusingRead.Add(control);

    Task<CameraControlState?> ICameraBackend.GetControlAsync(
        CameraControlKind control, CancellationToken ct)
    {
        EnsureOpen();
        var info = _controls.FirstOrDefault(c => c.Kind == control);
        if (info is null || _controlsRefusingRead.Contains(control))
            return Task.FromResult<CameraControlState?>(null);

        // A control nobody has touched sits wherever the device would leave it:
        // driving itself if it can, held at its default if it cannot. Modelling
        // that rather than defaulting to Manual matters, because a consumer
        // testing "did I restore what I found" against a fake that starts
        // everything Manual would pass without ever exercising the case.
        var value = _controlValues.TryGetValue(control, out var v) ? v : info.DefaultValue ?? 0;
        var mode = _controlModes.TryGetValue(control, out var m)
            ? m
            : info.SupportsAutoMode ? CameraControlMode.Automatic : CameraControlMode.Manual;

        return Task.FromResult<CameraControlState?>(new CameraControlState(control, value, mode));
    }

    Task ICameraBackend.SetControlAsync(CameraControlKind control, double value, CancellationToken ct)
    {
        EnsureOpen();
        var info = _controls.FirstOrDefault(c => c.Kind == control)
            ?? throw new CameraException($"Control {control} not found.");
        if (info.IsReadOnly)
            throw new CameraException($"Control {control} is read-only.");
        _controlValues[control] = value;
        // Writing a value takes the control out of the device's hands, matching
        // what the Media Foundation backend does with MF_CAMERA_FLAGS_MANUAL.
        _controlModes[control] = CameraControlMode.Manual;
        return Task.CompletedTask;
    }

    Task ICameraBackend.ResetControlAsync(CameraControlKind control, CancellationToken ct)
    {
        EnsureOpen();
        var info = _controls.FirstOrDefault(c => c.Kind == control)
            ?? throw new CameraException($"Control {control} not found.");
        if (info.DefaultValue.HasValue)
            _controlValues[control] = info.DefaultValue.Value;
        else
            _controlValues.Remove(control);

        // Reset hands the control back to the device where it has an automatic
        // mode to hand it back to.
        if (info.SupportsAutoMode)
            _controlModes[control] = CameraControlMode.Automatic;
        else
            _controlModes.Remove(control);

        return Task.CompletedTask;
    }

    Task ICameraBackend.ConfigureAsync(CameraConfiguration configuration, CancellationToken ct)
    {
        EnsureOpen();
        if (!_formats.Contains(configuration.Format))
            throw new CameraConfigurationException(
                $"Format {configuration.Format.Width}x{configuration.Format.Height} "
                    + $"{configuration.Format.PixelFormat} is not supported.");
        _configuration = configuration;
        return Task.CompletedTask;
    }

    Task ICameraBackend.StartCaptureAsync(CancellationToken ct)
    {
        EnsureOpen();
        if (_configuration is null)
            throw new InvalidOperationException("Device not configured.");
        Volatile.Write(ref _isCapturing, true);
        return Task.CompletedTask;
    }

    async Task<RawCameraFrame> ICameraBackend.ReadRawFrameAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (!Volatile.Read(ref _isCapturing))
            throw new InvalidOperationException("Capture not started.");

        if (FaultOnNextRead is { } ex)
        {
            FaultOnNextRead = null;
            throw ex;
        }

        if (HangOnRead || Volatile.Read(ref _frameCounter) >= MaxFrames)
        {
            _readHangReached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        if (FrameDelay > TimeSpan.Zero)
            await Task.Delay(FrameDelay, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        var format = OverridePixelFormat ?? _configuration!.Format.PixelFormat;
        int w = _configuration!.Format.Width;
        int h = _configuration.Format.Height;
        int frameIndex = Interlocked.Increment(ref _frameCounter);

        return GenerateFrame(w, h, format, frameIndex);
    }

    Task ICameraBackend.StopCaptureAsync()
    {
        Volatile.Write(ref _isCapturing, false);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _isCapturing, false);
        Volatile.Write(ref _isOpen, false);
        Volatile.Write(ref _disposed, true);
        return ValueTask.CompletedTask;
    }

    // ── Guards ─────────────────────────────────────────────────────────

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (!Volatile.Read(ref _isOpen))
            throw new InvalidOperationException("Device is not open. Call OpenAsync first.");
    }

    // ── Frame synthesis ────────────────────────────────────────────────

    // Size, stride, plane count, and plane offsets all come from
    // CameraFrameLayout / PlaneLayout — the same math the real backends and the
    // frame pool use. The fake used to keep its own bytes-per-pixel switch and it
    // drifted: NV12 was generated at 8 bits/px ("Y plane only for simplicity")
    // where it is 12, so a 640x480 NV12 frame was 307 200 bytes against a real
    // 460 800 and anything reading chroma ran off the end (#321). A second copy
    // of the layout math is how that recurs, so there isn't one any more.
    private RawCameraFrame GenerateFrame(int w, int h, CameraPixelFormat format, int frameIndex)
    {
        var spec = new CameraFrameSpec(format, w, h, ResolveStride(format, w), frameIndex);
        var data = (FrameFactory ?? CameraFramePatterns.FrameIndexConstant)(spec)
            ?? throw new InvalidOperationException("FrameFactory returned null.");

        // Checked here rather than left to the pool: a wrong length otherwise
        // surfaces as an out-of-range slice deep inside CameraFramePool.BuildPlanes,
        // pointing at the library instead of at the test's own generator.
        bool lengthOk = format == CameraPixelFormat.Mjpeg
            ? data.Length > 0
            : data.Length == spec.FrameSize;
        if (!lengthOk)
            throw new InvalidOperationException(
                $"FrameFactory returned {data.Length} bytes for a {w}x{h} {format} frame at "
                    + $"stride {spec.Stride}; expected {spec.FrameSize}.");

        return new RawCameraFrame
        {
            Data = data,
            Width = w,
            Height = h,
            PixelFormat = format,
            Timestamp = TimeSpan.FromMilliseconds(frameIndex * 33.333),
            PlaneCount = CameraFrameLayout.PlaneCount(format),
            Planes = PlaneLayout.DescribePlanes(format, w, h, spec.Stride),
        };
    }

    private int ResolveStride(CameraPixelFormat format, int w)
    {
        int natural = CameraFrameLayout.BytesPerRow(format, w);

        // MJPEG is a compressed blob with no rows, so a stride has nothing to
        // mean and CameraFrameLayout.FrameSize ignores it. Ignore it here too,
        // rather than validating a number that cannot apply — a test that sets
        // the hook once and sweeps formats should not trip over the one format
        // the hook does not reach.
        if (OverrideStride is not { } stride || format == CameraPixelFormat.Mjpeg)
            return natural;
        if (stride < natural)
            throw new InvalidOperationException(
                $"OverrideStride {stride} is narrower than the natural {natural}-byte row of a "
                    + $"{w}-pixel {format} frame.");
        return stride;
    }
}
