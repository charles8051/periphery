// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Periphery;

namespace Periphery.Camera.Avalonia;

/// <summary>
/// Avalonia control that hosts a live camera preview. Bind
/// <see cref="Device"/> to a picked <see cref="DeviceInfo"/> and the
/// control opens a session, runs the capture loop, decodes frames, and
/// renders them on a fixed 60 Hz cadence — no plumbing required.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architecture (mirrors frame-flow's <c>FrameFlowVideoView</c> /
/// <c>AvaloniaVideoSink</c>):</b>
/// </para>
/// <list type="bullet">
/// <item>The control implements <see cref="ICameraFrameSink"/> internally.
/// Producer threads call <see cref="ICameraFrameSink.PresentAsync"/>;
/// the latest frame is stored via <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// — newer arrivals supersede older pending frames (drop-on-overwrite).</item>
/// <item>A <see cref="DispatcherTimer"/> ticks at ~60 Hz on the UI thread
/// to invalidate the visual; the actual render pass picks up whatever
/// frame is currently pending.</item>
/// <item>The render override draws the front bitmap directly via
/// <see cref="DrawingContext.DrawImage(IImage, Rect)"/>, sized to the
/// control's bounds with uniform aspect.</item>
/// </list>
/// <para>
/// Internally owns a <see cref="DeviceSessionHost{TSession}"/> for
/// reconnect resilience, so unplug-replug cycles resume the preview
/// automatically. The control disposes the host when it's detached
/// from the visual tree.
/// </para>
/// <para>
/// <b>Formats.</b> The session is opened on the best format the camera
/// advertises that the control can display, chosen by
/// <see cref="PreviewFormatChoice"/>: <c>Bgra32</c> and <c>Rgba32</c> go
/// straight into a reused <see cref="WriteableBitmap"/> as a strided row
/// copy, <c>Mjpeg</c> is decoded by Skia, and <c>Yuy2</c> and <c>Nv12</c>
/// are converted to BGRA in-process. A camera that offers none of those
/// fails at <c>OpenAsync</c> with a message naming what it does offer.
/// </para>
/// <para>
/// <b>Threading — three threads, two handoffs.</b>
/// </para>
/// <list type="bullet">
/// <item><b>Capture</b> (an arbitrary thread-pool thread, not the same one
/// frame to frame) runs <see cref="RunCaptureLoopAsync"/> and therefore
/// <see cref="PresentAsync"/>: it converts or decodes the frame, writes the
/// pixels, and publishes the surface. One frame at a time; the loop awaits
/// each present before reading the next.</item>
/// <item><b>UI</b> runs the <see cref="DispatcherTimer"/> tick and
/// <see cref="Render"/>. <see cref="Render"/> claims the pending surface and
/// records a draw command — it does not draw.</item>
/// <item><b>Compositor</b> replays that command later and performs the actual
/// <c>DrawBitmap</c>, so the bitmap is read <i>after</i>
/// <see cref="Render"/> returns.</item>
/// </list>
/// <para>
/// Forward handoff: the capture thread publishes into <c>_pendingSurface</c>
/// with <see cref="Interlocked.Exchange{T}(ref T, T)"/> and the UI thread
/// claims it the same way. Return handoff: retired and superseded surfaces go
/// back to the capture thread through two more exchange slots so they can be
/// written again instead of reallocated. Two surfaces is the steady state, and
/// the capture thread never writes into the current front one.
/// </para>
/// <para>
/// A <i>just-retired</i> front can still be referenced by a draw list the
/// compositor has not replayed yet, and the capture thread may write into that
/// one. <c>WriteableBitmap.Lock()</c> and the compositor's <c>DrawBitmap</c>
/// take the same Skia monitor, so the write cannot tear against the replay; the
/// cost is that one of the two waits for the other, for the length of a
/// full-frame copy. The alternative — never recycling — is an allocation of
/// width × height × 4 bytes per frame, which is the thing this control was
/// changed to stop doing.
/// </para>
/// </remarks>
public sealed class CameraPreview : Control, ICameraFrameSink
{
    private static readonly IReadOnlyList<CameraFrameMemoryDomain> CpuOnly =
        new[] { CameraFrameMemoryDomain.Cpu };

    /// <summary>~60 Hz invalidation cadence. Matches frame-flow's <c>FrameFlowVideoView</c>.</summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(16);

    // ── Dependency properties ─────────────────────────────────────────

    /// <summary>The camera to preview. Setting <see langword="null"/> disconnects.</summary>
    public static readonly StyledProperty<DeviceInfo?> DeviceProperty =
        AvaloniaProperty.Register<CameraPreview, DeviceInfo?>(nameof(Device));

    /// <summary>
    /// Maximum resolution to negotiate at session open. Defaults to 1280×720
    /// because larger MJPEG modes can take seconds to spin up on commodity
    /// USB webcams.
    /// </summary>
    public static readonly StyledProperty<PixelSize> MaxResolutionProperty =
        AvaloniaProperty.Register<CameraPreview, PixelSize>(
            nameof(MaxResolution),
            defaultValue: new PixelSize(1280, 720));

    /// <summary><see langword="true"/> when a session is active and frames are flowing.</summary>
    public static readonly DirectProperty<CameraPreview, bool> IsLiveProperty =
        AvaloniaProperty.RegisterDirect<CameraPreview, bool>(
            nameof(IsLive), o => o.IsLive);

    /// <summary>UI-friendly description of the current host status. Bind to a status TextBlock.</summary>
    public static readonly DirectProperty<CameraPreview, string> StatusDescriptionProperty =
        AvaloniaProperty.RegisterDirect<CameraPreview, string>(
            nameof(StatusDescription), o => o.StatusDescription);

    /// <summary>The most recent open-time or capture-loop error. Cleared on successful reconnect.</summary>
    public static readonly DirectProperty<CameraPreview, Exception?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<CameraPreview, Exception?>(
            nameof(LastError), o => o.LastError);

    /// <inheritdoc cref="DeviceProperty"/>
    public DeviceInfo? Device
    {
        get => GetValue(DeviceProperty);
        set => SetValue(DeviceProperty, value);
    }

    /// <inheritdoc cref="MaxResolutionProperty"/>
    public PixelSize MaxResolution
    {
        get => GetValue(MaxResolutionProperty);
        set => SetValue(MaxResolutionProperty, value);
    }

    private bool _isLive;
    /// <inheritdoc cref="IsLiveProperty"/>
    public bool IsLive
    {
        get => _isLive;
        private set => SetAndRaise(IsLiveProperty, ref _isLive, value);
    }

    private string _statusDescription = "Idle.";
    /// <inheritdoc cref="StatusDescriptionProperty"/>
    public string StatusDescription
    {
        get => _statusDescription;
        private set => SetAndRaise(StatusDescriptionProperty, ref _statusDescription, value);
    }

    private Exception? _lastError;
    /// <inheritdoc cref="LastErrorProperty"/>
    public Exception? LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    // ── Internal state ──────────────────────────────────────────────

    private readonly CancellationTokenSource _lifetimeCts = new();
    private DeviceSessionHost<CameraSession>? _host;
    private Task? _pendingTransition;

    // Drop-on-overwrite: latest presented frame waiting to be rendered.
    // Atomic swap by both producer (PresentAsync) and consumer (Render).
    private PreviewSurface? _pendingSurface;
    // Currently displayed frame. Owned by the UI thread (only mutated in Render).
    private PreviewSurface? _frontSurface;

    // The two return slots of the surface rotation. Both hold a surface the
    // producer may write into again; both are exchanged rather than assigned so
    // DisposeFrameSlots can take them from the UI thread while the capture
    // thread is still running.
    //
    //   _spareSurface     a surface the producer published and then superseded
    //                     before the UI thread claimed it. Nothing outside the
    //                     capture thread ever saw it, so it is the safest one to
    //                     write into next.
    //   _recycledSurface  the surface Render retired from the front. Safe to
    //                     write into (the Skia monitor sees to that), but a draw
    //                     list may still reference it, so it is second choice.
    private PreviewSurface? _spareSurface;
    private PreviewSurface? _recycledSurface;

    // Which stream the slots belong to. Advanced by BeginStream before a capture
    // loop publishes anything, and read by the posted cleanups so one queued for
    // a stream that has already been replaced does nothing.
    //
    // Guarded by _streamLock rather than Interlocked, because the thing that has
    // to be atomic is "check the generation and then empty the slots" as one
    // step, not either half of it. A generation read that is atomic on its own
    // still lets a new stream start between the check and the disposal, and then
    // the disposal takes the new stream's surfaces with it (Peanut Gallery turn
    // 3). The lock is taken only when a stream starts and when the slots are
    // emptied -- never on the per-frame path, which keeps its Interlocked
    // handoff.
    private readonly object _streamLock = new();
    private long _streamGeneration;

    private DispatcherTimer? _renderTimer;
    private long _droppedFrameCount;
    private long _renderedFrameCount;
    // Set once when a frame arrives in a format the control cannot display, so
    // the capture loop does not post an identical LastError per frame. Cleared
    // by OnFormatChangedAsync, which is the only event that can change the
    // answer.
    private int _undisplayableFormatReported;

    /// <summary>Number of frames superseded by a newer arrival before the render thread picked them up.</summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    /// <summary>
    /// Number of distinct frames the render thread has actually drawn. Increments
    /// only when a new pending frame is swapped into the front buffer — re-paints
    /// of the existing front frame (e.g. layout changes, idle invalidations) don't
    /// count. Use this for FPS measurement.
    /// </summary>
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);

    static CameraPreview()
    {
        DeviceProperty.Changed.AddClassHandler<CameraPreview>((c, e) => c.OnDeviceChanged(e));
    }

    // ── ICameraFrameSink ────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<CameraFrameMemoryDomain> SupportedMemoryDomains => CpuOnly;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs on the capture thread — whatever thread the producer drives, which
    /// is not the same one frame to frame. Converts, decodes, or copies the
    /// pixels there, then atomically swaps the surface into the pending slot.
    /// A pending surface superseded before the UI thread claimed it is counted
    /// as dropped and kept for the next frame to write into — see
    /// <see cref="DroppedFrameCount"/>.
    /// </para>
    /// <para>
    /// The frame is disposed before this returns, on every path, per
    /// <see cref="ICameraFrameSink"/>'s ownership contract. Nothing here
    /// outlives the call: the pixels are copied into the surface rather than
    /// referenced, so the frame's buffer goes straight back to the pool.
    /// </para>
    /// </remarks>
    public ValueTask PresentAsync(ICameraFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            // Negotiation already restricted the session to a displayable
            // format, so this fires only on a mid-stream format switch — some
            // USB chipsets autoswap under bandwidth pressure. Dropping the
            // frame silently would leave a frozen picture and a status line
            // reading "Session active", so it is reported once.
            if (!PreviewPixelFormats.TryGetPath(
                    frame.PixelFormat, frame.Width, frame.Height, out var path))
            {
                ReportUndisplayableFormat(frame);
                return ValueTask.CompletedTask;
            }

            Publish(path == PreviewPixelPath.DecodeJpeg ? Decode(frame) : WritePixels(frame, path));
        }
        finally
        {
            // Sink owns the frame on PresentAsync per ICameraFrameSink contract.
            frame.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Skia decodes the compressed blob. Still one bitmap per frame: the decoder
    /// allocates its own output and there is nothing to write into.
    /// </summary>
    private static PreviewSurface Decode(ICameraFrame frame)
    {
        // ToArray copies the compressed bytes, which for a 720p webcam frame is
        // tens of kilobytes rather than the megabytes a raw frame would be. The
        // decode that follows dominates it.
        using var ms = new MemoryStream(frame.ContiguousBuffer.ToArray());
        return PreviewSurface.Decoded(new Bitmap(ms));
    }

    /// <summary>
    /// Writes the frame's pixels into a surface — reused when one of the right
    /// geometry and format is available, allocated when not.
    /// </summary>
    private PreviewSurface WritePixels(ICameraFrame frame, PreviewPixelPath path)
    {
        var key = PreviewSurfaceKey.For(frame.Width, frame.Height, path);
        var surface = Rent(key);
        try
        {
            surface.Write(frame, path);
        }
        catch
        {
            surface.Dispose();
            throw;
        }
        return surface;
    }

    /// <summary>
    /// Takes a surface matching <paramref name="key"/> out of the two return
    /// slots, or allocates one. A surface that no longer matches is disposed
    /// rather than kept: the key changes only when the stream's geometry or
    /// format does, and holding the old size would leak it for the life of the
    /// control.
    /// </summary>
    private PreviewSurface Rent(PreviewSurfaceKey key) =>
        Claim(ref _spareSurface, key) ?? Claim(ref _recycledSurface, key) ?? PreviewSurface.Create(key);

    private static PreviewSurface? Claim(ref PreviewSurface? slot, PreviewSurfaceKey key)
    {
        var candidate = Interlocked.Exchange(ref slot, null);
        if (candidate is null)
            return null;
        if (candidate.CanReuseFor(key))
            return candidate;
        candidate.Dispose();
        return null;
    }

    /// <summary>
    /// Hands the surface to the UI thread, and keeps whatever it displaced.
    /// </summary>
    private void Publish(PreviewSurface surface)
    {
        var stale = Interlocked.Exchange(ref _pendingSurface, surface);
        if (stale is null)
            return;

        // DroppedFrameCount counts frames superseded before the render thread
        // picked them up. Unchanged from the MJPEG-only path: one increment per
        // pending surface that never reached Render.
        Interlocked.Increment(ref _droppedFrameCount);
        Return(ref _spareSurface, stale);
    }

    /// <summary>
    /// Puts a surface into a return slot, or disposes it when the slot is full
    /// or the surface is a decoded bitmap that cannot be written into.
    /// </summary>
    private static void Return(ref PreviewSurface? slot, PreviewSurface surface)
    {
        if (surface.Writeable is null)
        {
            surface.Dispose();
            return;
        }

        var displaced = Interlocked.Exchange(ref slot, surface);
        displaced?.Dispose();
    }

    private void ReportUndisplayableFormat(ICameraFrame frame)
    {
        if (Interlocked.Exchange(ref _undisplayableFormatReported, 1) != 0)
            return;

        var error = new CameraConfigurationException(
            $"The camera switched to {frame.PixelFormat} at {frame.Width}x{frame.Height}, which "
                + $"CameraPreview cannot display. Displayable formats, most preferred first: "
                + $"{string.Join(", ", PreviewPixelFormats.Displayable)}.");
        Dispatcher.UIThread.Post(() => LastError = error);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drops the cached surfaces, which were sized and formatted for the stream
    /// that just ended. The pending and front surfaces are left alone — they
    /// still hold the last good picture, and replacing it with a blank control
    /// while the new format spins up is worse than showing a stale frame for a
    /// few milliseconds.
    /// <para>
    /// <b>The control calls this on itself</b>, from
    /// <see cref="RunCaptureLoopAsync"/>, before the first frame of a session.
    /// The pipeline runtime that <see cref="ICameraFrameSink"/> names as the
    /// caller was never built (ADR-0045), so nothing else would.
    /// </para>
    /// </remarks>
    public ValueTask OnFormatChangedAsync(CameraFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);

        Interlocked.Exchange(ref _spareSurface, null)?.Dispose();
        Interlocked.Exchange(ref _recycledSurface, null)?.Dispose();
        Interlocked.Exchange(ref _undisplayableFormatReported, 0);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Typical consumers don't call this directly — disposal is driven by
    /// <see cref="OnDetachedFromVisualTree"/>. Implementing
    /// <see cref="ICameraFrameSink"/> requires it; idempotent with the
    /// detach-driven cleanup.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        DisposeFrameSlots();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes the surfaces only if <paramref name="stream"/> is still the
    /// current one.
    /// </summary>
    /// <remarks>
    /// Session-end and disconnect cleanup is <i>posted</i> to the UI thread and
    /// the poster does not wait for it. The host can reconnect, and the next
    /// capture loop can publish its first frame, while that cleanup is still
    /// sitting in the dispatcher queue — at which point running it would drop
    /// the new stream's first frame and dispose a surface the capture thread is
    /// writing into. The generation says which stream the cleanup was for, and
    /// a cleanup for a stream that has been replaced does nothing (Peanut
    /// Gallery turn 1).
    /// <para>
    /// Skipping is safe rather than a leak: the surfaces the old stream left in
    /// the slots are reclaimed by the ordinary rotation — a stale pending
    /// surface is superseded on the next publish, a stale front is retired on
    /// the next render, and a stale spare or recycled surface is disposed by
    /// <see cref="Claim"/> when its key does not match.
    /// </para>
    /// </remarks>
    private void DisposeFrameSlotsIfCurrent(long stream)
    {
        lock (_streamLock)
        {
            if (_streamGeneration != stream)
                return;
            DisposeFrameSlotsCore();
        }
    }

    /// <summary>
    /// The current stream's generation. Only the disconnect path uses this, and
    /// only because there is no stream left to ask.
    /// </summary>
    /// <remarks>
    /// Safe there and nowhere else. <see cref="OnDeviceChanged"/> reads it after
    /// <c>DisconnectHostAsync</c> has been awaited and before its own transition
    /// task completes, and a transition for the next device waits on that task
    /// before it connects. So no stream can begin between this read and the post.
    /// A capture loop, by contrast, must carry the generation
    /// <see cref="BeginStream"/> gave it rather than read this one at the end
    /// (Peanut Gallery turn 4).
    /// </remarks>
    private long CurrentStream()
    {
        lock (_streamLock)
            return _streamGeneration;
    }

    /// <summary>
    /// Claims the next stream generation and returns it. Called before a capture
    /// loop publishes anything, so a cleanup for the previous stream either
    /// completes before this returns or finds the generation moved on and does
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The caller keeps the returned value for the life of its stream and hands
    /// it back when it posts its own cleanup. Reading the current generation at
    /// cleanup time instead would name whichever stream happened to be current
    /// then, which is not necessarily the one that ended (Peanut Gallery turn 4).
    /// </remarks>
    private long BeginStream()
    {
        lock (_streamLock)
            return ++_streamGeneration;
    }

    private void DisposeFrameSlots()
    {
        lock (_streamLock)
            DisposeFrameSlotsCore();
    }

    private void DisposeFrameSlotsCore()
    {
        // The slots themselves stay Interlocked: the capture thread can be in
        // Claim on the same field, and exactly one of the two takes the surface.
        Interlocked.Exchange(ref _pendingSurface, null)?.Dispose();
        Interlocked.Exchange(ref _spareSurface, null)?.Dispose();
        Interlocked.Exchange(ref _recycledSurface, null)?.Dispose();

        // _frontSurface is only mutated on the UI thread in Render; safe to
        // null-and-dispose here when the control is leaving the visual tree.
        var front = _frontSurface;
        _frontSurface = null;
        front?.Dispose();
    }

    // ── Render pass ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Pull the latest pending frame; supersede the front if newer.
        var newest = Interlocked.Exchange(ref _pendingSurface, null);
        if (newest is not null)
        {
            var retired = _frontSurface;
            _frontSurface = newest;
            Interlocked.Increment(ref _renderedFrameCount);
            // Back to the capture thread rather than disposed. A draw list
            // recorded before this swap may still reference it; the Skia
            // monitor the producer's Lock() takes covers that.
            if (retired is not null)
                Return(ref _recycledSurface, retired);
        }

        var front = _frontSurface;
        if (front is null) return;

        // Uniform aspect within Bounds.
        var bounds = Bounds;
        var dest = ComputeUniformDestination(front.Image.Size, new Size(bounds.Width, bounds.Height));
        if (dest.Width <= 0 || dest.Height <= 0) return;

        context.DrawImage(front.Image, dest);
    }

    private static Rect ComputeUniformDestination(Size source, Size target)
    {
        if (source.Width <= 0 || source.Height <= 0) return default;
        var srcAspect = source.Width / source.Height;
        var dstAspect = target.Width / target.Height;
        double width, height;
        if (srcAspect > dstAspect)
        {
            width = target.Width;
            height = target.Width / srcAspect;
        }
        else
        {
            height = target.Height;
            width = target.Height * srcAspect;
        }
        var x = (target.Width - width) / 2;
        var y = (target.Height - height) / 2;
        return new Rect(x, y, width, height);
    }

    // ── Lifecycle ──────────────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Without a periodic invalidation, Avalonia would render the control
        // exactly once on attach. The timer drives a fixed 60 Hz redraw
        // cadence regardless of camera frame rate; Render decides whether
        // there's actually a new frame to show.
        _renderTimer = new DispatcherTimer(
            RenderInterval,
            DispatcherPriority.Render,
            (_, _) => InvalidateVisual());
        _renderTimer.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _renderTimer?.Stop();
        _renderTimer = null;

        // Cancel everything; the host's bounded DisposeAsync still
        // runs in the background but won't hold the process open.
        _lifetimeCts.Cancel();
        _ = DetachAsync();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task DetachAsync()
    {
        await DisconnectHostAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(DisposeFrameSlots);
    }

    // ── Device-property change handling ───────────────────────────────

    private async void OnDeviceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        // Serialize transitions so a rapid Device flip can't end up with
        // two hosts running.
        var newDevice = (DeviceInfo?)e.NewValue;
        var prior = _pendingTransition;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTransition = tcs.Task;

        try
        {
            if (prior is not null)
            {
                try { await prior.ConfigureAwait(false); } catch { /* prior fault is its own concern */ }
            }

            await DisconnectHostAsync().ConfigureAwait(false);

            if (newDevice is null || _lifetimeCts.IsCancellationRequested)
            {
                long stream = CurrentStream();
                Dispatcher.UIThread.Post(() =>
                {
                    DisposeFrameSlotsIfCurrent(stream);
                    InvalidateVisual();
                    StatusDescription = "Idle.";
                });
                return;
            }

            await ConnectAsync(newDevice).ConfigureAwait(false);
        }
        finally
        {
            tcs.TrySetResult();
        }
    }

    private async Task ConnectAsync(DeviceInfo device)
    {
        try
        {
            _host = await DeviceSessionHost<CameraSession>.ForDeviceAsync(
                device,
                createSession: CreateSessionAsync,
                // No onSessionEnded hook: the capture loop posts its own
                // cleanup from its finally, tagged with the generation it
                // claimed. A hook here would have to work out which stream had
                // ended from outside the stream, and cannot.
                whileSessionActive: RunCaptureLoopAsync,
                ct: _lifetimeCts.Token).ConfigureAwait(false);

            _host.StatusChanged += OnHostStatusChanged;
            var initialStatus = _host.Status;
            var initialDescription = _host.StatusDescription;
            Dispatcher.UIThread.Post(() => ApplyHostStatus(initialStatus, initialDescription));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                LastError = ex;
                StatusDescription = $"Failed to start: {ex.Message}";
                IsLive = false;
            });
        }
    }

    private async Task DisconnectHostAsync()
    {
        if (_host is null) return;
        _host.StatusChanged -= OnHostStatusChanged;
        var host = _host;
        _host = null;

        try { await host.DisposeAsync().ConfigureAwait(false); }
        catch { /* shutdown best-effort */ }

        Dispatcher.UIThread.Post(() =>
        {
            IsLive = false;
            StatusDescription = "Idle.";
        });
    }

    // ── Session factory + capture loop ────────────────────────────────

    private Task<CameraSession> CreateSessionAsync(DeviceInfo device, CancellationToken ct)
    {
        var max = Dispatcher.UIThread.CheckAccess()
            ? MaxResolution
            : Dispatcher.UIThread.InvokeAsync(() => MaxResolution).GetTask().GetAwaiter().GetResult();

        // UseFormat rather than the fluent criteria: neither AllowOnlyPixelFormats
        // nor PreferPixelFormat expresses a ranked set, and the choice between
        // "decode this" and "convert that" is exactly a ranking. UseFormat takes
        // precedence over MaxResolution, so the box is applied inside Select.
        return CameraSession
            .For(device)
            .UseFormat(snapshot =>
                PreviewFormatChoice.Select(snapshot.Formats, max.Width, max.Height)
                ?? throw new CameraConfigurationException(
                    PreviewFormatChoice.DescribeNoMatch(snapshot.Formats, max.Width, max.Height),
                    device.Id))
            .OpenAsync(ct);
    }

    private async Task RunCaptureLoopAsync(CameraSession session, CancellationToken ct)
    {
        // Claimed before anything is published, and held for the life of this
        // loop. The loop is the only thing that knows which stream its surfaces
        // belong to, so it is the thing that posts their cleanup.
        long stream = BeginStream();

        try
        {
            // ICameraFrameSink says OnFormatChangedAsync is called before any
            // PresentAsync in the new format. The pipeline runtime that would
            // do so was never built (ADR-0045), so the control does it itself —
            // and does it again after every reconnect, which is where the
            // camera can come back in a different format.
            var format = session.Configuration.Format;
            await OnFormatChangedAsync(
                new CameraFormatInfo(format.Width, format.Height, format.PixelFormat), ct)
                .ConfigureAwait(false);

            // Each frame goes through the sink's PresentAsync, which converts or
            // decodes, atomically swaps into the pending slot, and disposes the
            // lease. The DispatcherTimer drives the actual render at ~60 Hz.
            await foreach (var frame in session.CaptureAsync(ct: ct).ConfigureAwait(false))
            {
                await PresentAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — session ended or control detached.
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => LastError = ex);
        }
        finally
        {
            // This stream is over: drop its surfaces, unless a later one has
            // already taken the slots. Posted rather than run here because
            // _frontSurface belongs to the UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                DisposeFrameSlotsIfCurrent(stream);
                InvalidateVisual();
            });
        }
    }

    // ── Host status ──────────────────────────────────────────────────

    private void OnHostStatusChanged(object? sender, HostStatus<CameraSession> status)
    {
        if (sender is not DeviceSessionHost<CameraSession> host) return;
        var description = host.StatusDescription;
        Dispatcher.UIThread.Post(() => ApplyHostStatus(status, description));
    }

    private void ApplyHostStatus(HostStatus<CameraSession> status, string description)
    {
        StatusDescription = description;
        IsLive = status is SessionActive<CameraSession>;
        if (status is SessionUnavailable<CameraSession> { LastError: not null } unavailable)
            LastError = unavailable.LastError;
        else if (status is SessionActive<CameraSession>)
            LastError = null;
    }
}
