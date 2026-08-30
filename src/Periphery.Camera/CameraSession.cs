// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Camera.Internal;

namespace Periphery.Camera;

/// <summary>
/// Configured, frame-producing capture runtime. This is the preferred
/// application-facing capture primitive.
/// </summary>
/// <remarks>
/// <para>
/// Two construction paths are supported:
/// </para>
/// <list type="bullet">
/// <item><see cref="OpenAsync"/> — convenience path that creates and owns the
/// underlying <see cref="CameraDevice"/>. Disposing the session disposes the
/// device.</item>
/// <item><see cref="CameraDevice.OpenSessionAsync"/> — advanced path where the
/// caller owns the device independently. Disposing the session does not dispose
/// the device.</item>
/// </list>
/// <para>
/// Internally, a dedicated producer thread reads frames from the backend as fast
/// as the driver delivers them and enqueues them into a bounded
/// <see cref="Channel{T}"/>. The consumer (application) reads from the channel.
/// The buffering absorbs consumer jitter; it does not make the session lossless.
/// </para>
/// <para>
/// <b>Delivery is lossy by contract (ADR-0082 D1).</b> A consumer must not assume
/// it received every frame the camera produced. Frames are lost in two places and
/// only one of them is countable:
/// </para>
/// <list type="bullet">
/// <item>Inside Periphery, when the pipeline is full. Which frame goes is
/// <see cref="CameraSessionOptions.ExhaustionPolicy"/>'s decision, and every such
/// loss lands in <see cref="CameraSessionMetrics.FramesDropped"/>.</item>
/// <item>Inside the platform, when the producer is not calling the driver's read
/// fast enough. A camera is a real-time source — the sensor keeps exposing whether
/// or not anyone is reading — so the driver's queue saturates and it discards.
/// Periphery never sees those frames and cannot count them. Under
/// <see cref="BufferExhaustionPolicy.StallProducer"/> this is where the loss goes,
/// which is why stalling is not a delivery guarantee.
/// <see cref="CameraSessionMetrics.ProducerStallTime"/> is the signal that it is
/// happening.</item>
/// </list>
/// <para>
/// Frame timestamps come from the backend and are not contiguous across a drop.
/// A consumer that needs to know it missed something must compare them; a
/// consumer that needs to miss nothing needs a demand-paced source, which
/// Periphery does not offer (ADR-0082 D6).
/// </para>
/// <para>
/// A session is single-capture: only one <see cref="CaptureAsync"/> enumeration
/// or <see cref="StartCaptureAsync"/>/<see cref="ReadFrameAsync"/> sequence may
/// be active at a time.
/// </para>
/// </remarks>
public sealed partial class CameraSession : IAsyncDisposable
{
    private readonly ICameraBackend _backend;
    private readonly bool _ownsDevice;
    private readonly CameraFramePool _pool;
    private readonly ILogger<CameraSession> _logger;

    /// <summary>
    /// Shell-owned clock. Every timeout, delay, and elapsed-duration measurement
    /// in this session is expressed over this provider rather than wall-clock
    /// primitives (<see cref="System.Diagnostics.Stopwatch"/>,
    /// <c>new CancellationTokenSource(timeout)</c>, <c>Task.Delay(timeout)</c>),
    /// so the timeout-vs-cancellation decision is deterministically testable
    /// with a <c>FakeTimeProvider</c> (ADR-0052; review finding 2.2). Defaults
    /// to <see cref="TimeProvider.System"/> at the public entry points, so
    /// callers are unaffected.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    private Channel<LeasedCameraFrame>? _channel;
    private Task? _producerTask;
    private CancellationTokenSource? _producerCts;
    private Exception? _captureFault;
    private int _captureMode; // 0=Idle, 1=Streaming, 2=Pull

    /// <summary>
    /// Serialises the producer lifecycle. Producer <em>start</em>
    /// (<see cref="StartProducerAsync"/>) and producer <em>stop</em>
    /// (<see cref="StopProducerAsync"/>) both run under this lock, so they are
    /// mutually exclusive: a stop can never null the producer fields
    /// (<see cref="_producerCts"/>, <see cref="_channel"/>) while a start is
    /// half-way through publishing them, and the two stop paths
    /// (<see cref="CaptureAsync"/>'s <c>finally</c> and
    /// <see cref="DisposeAsync"/>) cannot race each other to dispose/null.
    /// Repro for the races this closes: a device-lost mid-capture where the
    /// producer faults and the router disposes the session in response.
    /// Never disposed — see <see cref="DisposeAsync"/>.
    /// </summary>
    private readonly SemaphoreSlim _stopLock = new(1, 1);

    private long _framesProduced;
    private long _producerStalls;
    private long _producerStallTicks;
    private TimeSpan? _lastTimestamp;
    private bool _disposed;

    internal CameraSession(
        CameraDevice device,
        bool ownsDevice,
        ICameraBackend backend,
        CameraConfiguration configuration,
        CameraSessionOptions options,
        ILogger<CameraSession>? logger = null,
        TimeProvider? timeProvider = null)
    {
        Device = device;
        _ownsDevice = ownsDevice;
        _backend = backend;
        Configuration = configuration;
        Options = options;
        _logger = logger ?? NullLogger<CameraSession>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pool = new CameraFramePool();

        int estimatedSize = EstimateFrameSize(configuration.Format);
        _pool.Seed(estimatedSize, PoolSizeFor(options));
    }

    // ── Properties ─────────────────────────────────────────────────────

    public CameraDevice Device { get; }
    public DeviceInfo DeviceInfo => Device.DeviceInfo;
    public CameraConfiguration Configuration { get; }
    public CameraSessionOptions Options { get; }
    public bool IsCapturing => Volatile.Read(ref _captureMode) != 0;

    public CameraSessionMetrics Metrics => new(
        Volatile.Read(ref _framesProduced),
        _pool.FramesDropped,
        _pool.OutstandingLeases,
        _lastTimestamp,
        Volatile.Read(ref _producerStalls),
        TimeSpan.FromTicks(Volatile.Read(ref _producerStallTicks)));

    // ── Convenience factory ────────────────────────────────────────────

    /// <summary>
    /// Entry point for the fluent <see cref="CameraSessionBuilder"/>. Use
    /// when you want discoverable shortcuts for format selection
    /// (<c>PreferMjpeg</c>, <c>MaxResolution</c>, …) or a snapshot-aware
    /// <c>UseFormat</c> delegate. For typed-record construction, call
    /// <see cref="OpenAsync(DeviceInfo, CameraConfiguration, CameraSessionOptions?, CancellationToken, ILogger{CameraSession}?, TimeProvider?)"/>
    /// directly. See ADR-0040.
    /// </summary>
    public static CameraSessionBuilder For(DeviceInfo device) => new(device);

    public static async Task<CameraSession> OpenAsync(
        DeviceInfo device,
        CameraConfiguration configuration,
        CameraSessionOptions? sessionOptions = null,
        CancellationToken ct = default,
        ILogger<CameraSession>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(configuration);

        var cameraDevice = await CameraDevice.OpenAsync(device, ct).ConfigureAwait(false);
        try
        {
            await cameraDevice._backend.ConfigureAsync(configuration, ct).ConfigureAwait(false);
            var session = new CameraSession(cameraDevice, ownsDevice: true, cameraDevice._backend,
                configuration, sessionOptions ?? new(), logger, timeProvider);
            session.LogSessionOpened();
            return session;
        }
        catch
        {
            await cameraDevice.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void LogSessionOpened()
    {
        _logger.LogInformation(
            "Camera session opened: {DeviceName} at {Width}x{Height} {PixelFormat} @ {FrameRate} fps (BufferCount={BufferCount}, ExhaustionPolicy={ExhaustionPolicy})",
            Device.DeviceInfo.Name ?? "(unnamed)",
            Configuration.Format.Width,
            Configuration.Format.Height,
            Configuration.Format.PixelFormat,
            Configuration.Format.MaxFrameRate,
            Options.BufferCount,
            Options.ExhaustionPolicy);
    }

    // ── Streaming capture (IAsyncEnumerable) ───────────────────────────

    public async IAsyncEnumerable<LeasedCameraFrame> CaptureAsync(
        CameraCaptureOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClaimMode(1);

        var effectiveOptions = options ?? new CameraCaptureOptions();
        var frameTimeout = effectiveOptions.EffectiveFrameTimeout;

        await _backend.StartCaptureAsync(ct).ConfigureAwait(false);

        // StartProducerAsync wires up the producer under _stopLock and hands
        // back the channel it created, so the read loop below operates on a
        // local that a concurrent StopProducerAsync can never null out from
        // under it (the former start-vs-dispose NRE).
        var channel = await StartProducerAsync(ct).ConfigureAwait(false);

        try
        {
            while (true)
            {
                bool hasMore;
                try
                {
                    hasMore = await WaitForNextFrameAsync(channel, frameTimeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }

                if (!hasMore)
                {
                    var fault = Volatile.Read(ref _captureFault);
                    if (fault is not null)
                        ExceptionDispatchInfo.Capture(fault).Throw();
                    yield break;
                }

                while (channel.Reader.TryRead(out var frame))
                {
                    RecordFrame(frame.Timestamp);
                    yield return frame;
                }
            }
        }
        finally
        {
            await StopProducerAsync().ConfigureAwait(false);
            ReleaseMode();
        }
    }

    /// <summary>
    /// Waits for the next frame to arrive in the channel, throwing
    /// <see cref="CameraTimeoutException"/> if <paramref name="timeout"/> elapses
    /// first. Treats user-cancellation and timeout-cancellation distinctly:
    /// the former propagates as <see cref="OperationCanceledException"/>; the
    /// latter throws a typed timeout error so callers can distinguish a
    /// stalled stream from a deliberate stop.
    /// </summary>
    /// <remarks>
    /// This is the imperative shell for the next-frame wait: it owns the
    /// timeout <see cref="CancellationTokenSource"/> and the await. The
    /// timeout timer is created over the session's injected
    /// <see cref="_timeProvider"/> (via the
    /// <see cref="CancellationTokenSource(TimeSpan, TimeProvider)"/> overload),
    /// so a <c>FakeTimeProvider</c> can drive the expiry branch by advancing
    /// virtual time — no real-millisecond sleep. The actual
    /// timeout-vs-cancelled classification is the pure
    /// <see cref="ClassifyWaitOutcome"/> helper, kept separate so the decision
    /// is testable as a value transform (ADR-0052; review finding 2.2).
    /// </remarks>
    private async Task<bool> WaitForNextFrameAsync(
        Channel<LeasedCameraFrame> channel, TimeSpan? timeout, CancellationToken ct)
    {
        if (timeout is null)
        {
            return await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(timeout.Value, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (ClassifyWaitOutcome(timeoutCts.IsCancellationRequested, ct.IsCancellationRequested)
                  == WaitOutcome.TimedOut)
        {
            throw new CameraTimeoutException(
                $"No frame received within {timeout.Value.TotalSeconds:F1}s. " +
                $"The camera may have stalled or stopped streaming.",
                Device.DeviceInfo.Id);
        }
    }

    /// <summary>
    /// The outcome of a bounded next-frame wait once its cancellation signals
    /// have settled.
    /// </summary>
    internal enum WaitOutcome
    {
        /// <summary>The wait was cancelled by the caller's token — propagate
        /// <see cref="OperationCanceledException"/> unchanged.</summary>
        Cancelled,

        /// <summary>The frame-timeout elapsed without caller cancellation —
        /// surface <see cref="CameraTimeoutException"/>.</summary>
        TimedOut,
    }

    /// <summary>
    /// Pure decision: given the two cancellation signals observed after a
    /// frame-wait unwound (the timeout token and the caller token), decide
    /// whether the wait <see cref="WaitOutcome.TimedOut"/> or was
    /// <see cref="WaitOutcome.Cancelled"/> by the caller. Caller-cancellation
    /// always wins: an <see cref="OperationCanceledException"/> is only
    /// reclassified as a timeout when the timeout fired and the caller did
    /// <em>not</em> cancel. Total over its inputs, no IO/clock/state — the
    /// timeout vs. cancellation classification a <c>FakeTimeProvider</c> drives
    /// (ADR-0052; review finding 2.2).
    /// </summary>
    internal static WaitOutcome ClassifyWaitOutcome(bool timeoutRequested, bool callerRequested) =>
        timeoutRequested && !callerRequested ? WaitOutcome.TimedOut : WaitOutcome.Cancelled;

    // ── Pull-based capture ─────────────────────────────────────────────

    public async Task StartCaptureAsync(
        CameraCaptureOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClaimMode(2);

        await _backend.StartCaptureAsync(ct).ConfigureAwait(false);
        await StartProducerAsync().ConfigureAwait(false);
    }

    public async Task<LeasedCameraFrame> ReadFrameAsync(
        CameraCaptureOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _captureMode) != 2)
            throw new InvalidOperationException("Capture is not active. Call StartCaptureAsync first.");

        var effectiveOptions = options ?? new CameraCaptureOptions();
        var frameTimeout = effectiveOptions.EffectiveFrameTimeout;

        // Snapshot once per ReadFrame call (same rationale as CaptureAsync —
        // protects the read loop from a concurrent StopProducerAsync nulling
        // _channel mid-iteration). The producer's channel completion still
        // surfaces through the captured reference.
        var channel = _channel
            ?? throw new InvalidOperationException("Capture has ended.");

        while (true)
        {
            if (!await WaitForNextFrameAsync(channel, frameTimeout, ct).ConfigureAwait(false))
            {
                var fault = Volatile.Read(ref _captureFault);
                if (fault is not null)
                    ExceptionDispatchInfo.Capture(fault).Throw();
                throw new InvalidOperationException("Capture has ended.");
            }

            if (channel.Reader.TryRead(out var frame))
            {
                RecordFrame(frame.Timestamp);
                return frame;
            }
        }
    }

    public async Task StopCaptureAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _captureMode) == 0) return;

        await StopProducerAsync().ConfigureAwait(false);
        ReleaseMode();
    }

    // ── Producer loop ──────────────────────────────────────────────────

    /// <summary>
    /// Creates the channel + producer task for a capture, publishes them to the
    /// producer fields, and returns the freshly-created channel so callers hold
    /// it as a local rather than re-reading <see cref="_channel"/> (which a
    /// concurrent <see cref="StopProducerAsync"/> may already have nulled).
    /// </summary>
    /// <remarks>
    /// Runs under <see cref="_stopLock"/> — the same lock
    /// <see cref="StopProducerAsync"/> takes — so producer start and producer
    /// stop are mutually exclusive. This makes the start-vs-dispose race
    /// structurally impossible: previously a <see cref="DisposeAsync"/>-driven
    /// stop could <see cref="Interlocked"/>.Exchange the producer fields to null
    /// <em>between</em> these writes and the caller's read, NRE-ing the capture
    /// on a half-nulled <see cref="_channel"/> / <see cref="_producerCts"/>. We
    /// also re-check <see cref="_disposed"/> under the lock so a dispose that won
    /// the race aborts the start cleanly with <see cref="ObjectDisposedException"/>
    /// instead of wiring up a producer it will never tear down.
    /// </remarks>
    private async Task<Channel<LeasedCameraFrame>> StartProducerAsync(CancellationToken externalCt = default)
    {
        await _stopLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Hold the freshly-created instances as locals: the producer task
            // and the calling read loop operate on these, never on the
            // (concurrently-nullable) fields, once we leave the lock.
            var cts = externalCt.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
                : new CancellationTokenSource();

            // FullMode is the policy (ADR-0082 D3). itemDropped is what makes
            // the drop mode usable with pooled frames at all: the channel hands
            // the evicted frame back and forgets it, so nothing else would ever
            // return its buffer and the pool would die after BufferCount drops.
            // This is also the whole eviction mechanism — no producer-side
            // Reader.TryRead, so SingleReader stays honest (#323).
            var channel = Channel.CreateBounded<LeasedCameraFrame>(
                new BoundedChannelOptions(Options.QueueDepth)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = FullModeFor(Options.ExhaustionPolicy),
                },
                itemDropped: OnFrameEvicted);

            _producerCts = cts;
            _channel = channel;
            _captureFault = null;
            // LongRunning gives the producer its own dedicated thread instead
            // of a thread-pool slot. Critical for backends like MF whose source
            // reader is single-threaded — every call to ReadRawFrameAsync runs
            // synchronously on the producer thread, so they all happen on the
            // same OS thread.
            _producerTask = Task.Factory.StartNew(
                () => ProducerLoopAsync(channel, cts.Token),
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            return channel;
        }
        finally
        {
            _stopLock.Release();
        }
    }

    private async Task ProducerLoopAsync(Channel<LeasedCameraFrame> channel, CancellationToken ct)
    {
        // One scope per bounded operation (the producer's lifetime). Every
        // log emitted inside inherits Session and Endpoint structured props
        // so consumers can correlate frame-level Trace logs with the
        // session that produced them — see logging-and-diagnostics.md §3.
        using var scope = _logger.BeginScope(
            "Session={DeviceId} Endpoint={NativeEndpoint}",
            Device.DeviceInfo.Id, Device.NativeEndpointId);

        LogProducerStarted(_logger, _backend.GetType().Name, Options.BufferCount);

        // The channel is passed in by StartProducerAsync: the producer operates
        // on the channel it was created with for its whole lifetime. A concurrent
        // StopProducerAsync may null the _channel field, but completion is
        // signalled via TryComplete() in the finally block of this method, so
        // the consumer still sees end-of-stream cleanly.
        //
        // Duration is measured off the injected clock (GetTimestamp/
        // GetElapsedTime) rather than Stopwatch, so the producer's elapsed-time
        // reporting is driven by the same TimeProvider as every other timing in
        // the session (review finding 2.2).
        var startTimestamp = _timeProvider.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var raw = await _backend.ReadRawFrameAsync(ct).ConfigureAwait(false);
                var frame = await DeliverFrameAsync(raw, ct).ConfigureAwait(false);
                if (frame is null) continue;

                try
                {
                    await WriteFrameAsync(channel, frame, ct).ConfigureAwait(false);
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — not a fault.
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _captureFault, ex);
            LogProducerFaulted(_logger, Device.NativeEndpointId, ex);
        }
        finally
        {
            channel.Writer.TryComplete();
            LogProducerStopped(
                _logger,
                Volatile.Read(ref _framesProduced),
                _pool.FramesDropped,
                Volatile.Read(ref _producerStalls),
                TimeSpan.FromTicks(Volatile.Read(ref _producerStallTicks)).TotalSeconds,
                _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
        }
    }

    /// <summary>
    /// How many buffers a session seeds its pool with:
    /// <c>BufferCount + QueueDepth + 1</c>. Pure, so the arithmetic the whole
    /// latest-wins guarantee rests on is assertable without a running session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame is free, queued, or leased — one budget, three states (ADR-0082
    /// D3). Each term buys one of them:
    /// </para>
    /// <list type="bullet">
    /// <item><c>BufferCount</c> — what the consumer may hold at once.</item>
    /// <item><c>QueueDepth</c> — the frames waiting in the channel. Reserved, so
    /// a queued frame never occupies a buffer the consumer was promised.</item>
    /// <item><c>+ 1</c> — the spare the producer copies the next frame into.
    /// This is the term that makes latest-wins true rather than aspirational.
    /// Eviction is the channel's, and the channel only evicts on a write, and a
    /// write needs a frame, and a frame needs a buffer. Without a spare, a
    /// consumer holding <c>BufferCount</c> leases against a full queue empties
    /// the pool, the copy is refused before the write that would have evicted
    /// anything, and the stale queued frame survives while every newer frame is
    /// dropped — which is <c>DropIncoming</c>, the member D2 deleted for exactly
    /// that behaviour. With the spare the producer can always copy, always
    /// write, and always trade the oldest queued frame for the newest; the
    /// evicted frame's buffer becomes the next spare.</item>
    /// </list>
    /// <para>
    /// The alternative — evicting from the queue before acquiring a buffer —
    /// needs the producer to read the channel, which is the
    /// <c>SingleReader = true</c> violation this change deletes (#323). One
    /// buffer is the cheaper answer.
    /// </para>
    /// <para>
    /// So the pool can only run dry when the consumer holds <em>more</em> than
    /// <c>BufferCount</c> frames — by <see cref="LeasedCameraFrame.AddRef"/>
    /// fan-out, or by not disposing — and that drop is the one no policy can
    /// prevent, because an active lease is never revoked (ADR-0035 D9).
    /// </para>
    /// </remarks>
    internal static int PoolSizeFor(CameraSessionOptions options) =>
        options.BufferCount + options.QueueDepth + 1;

    /// <summary>
    /// Which <see cref="BoundedChannelFullMode"/> a policy means. Pure and total
    /// over the enum, so the mapping that makes
    /// <see cref="BufferExhaustionPolicy"/> load-bearing at all can be asserted
    /// as a value transform rather than inferred from a running session
    /// (ADR-0082 D3).
    /// </summary>
    internal static BoundedChannelFullMode FullModeFor(BufferExhaustionPolicy policy) =>
        policy switch
        {
            BufferExhaustionPolicy.StallProducer => BoundedChannelFullMode.Wait,
            _ => BoundedChannelFullMode.DropOldest,
        };

    /// <summary>
    /// Copies a raw frame into a pooled buffer. Returns null when the frame was
    /// dropped, which the caller treats as "read the next one".
    /// </summary>
    /// <remarks>
    /// The pool refusing means the consumer is holding more than
    /// <see cref="CameraSessionOptions.BufferCount"/> frames — see
    /// <see cref="PoolSizeFor"/> for why nothing else can empty it. Under
    /// <see cref="BufferExhaustionPolicy.LatestWins"/> that is the one drop
    /// eviction cannot cover: a frame needs a buffer before it can reach the
    /// channel at all, and the only buffers left are inside leases this session
    /// will not revoke (ADR-0035 D9).
    /// </remarks>
    private async Task<LeasedCameraFrame?> DeliverFrameAsync(RawCameraFrame raw, CancellationToken ct)
    {
        var frame = _pool.TryDeliver(in raw);
        if (frame is not null) return frame;

        if (Options.ExhaustionPolicy is BufferExhaustionPolicy.StallProducer)
        {
            await StallAsync(_pool.WaitForReturnAsync(ct)).ConfigureAwait(false);

            // The pool signals returns rather than free buffers, so one wait
            // does not promise one buffer. A second null is a real drop.
            frame = _pool.TryDeliver(in raw);
            if (frame is not null) return frame;
        }

        CountDrop();
        return null;
    }

    /// <summary>
    /// Enqueues a frame for the consumer, timing the write when it parks. Under
    /// <see cref="BufferExhaustionPolicy.LatestWins"/> the write always completes
    /// synchronously — the channel evicts instead of waiting — so a write that
    /// does not is exactly a producer stall, and the fast path costs no clock
    /// reads.
    /// </summary>
    private async Task WriteFrameAsync(
        Channel<LeasedCameraFrame> channel, LeasedCameraFrame frame, CancellationToken ct)
    {
        var write = channel.Writer.WriteAsync(frame, ct);
        if (write.IsCompleted)
        {
            await write.ConfigureAwait(false);
            return;
        }

        await StallAsync(write.AsTask()).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits a producer-side wait and records it as a stall if it actually
    /// parks. The count goes up on entry, so a session parked right now already
    /// reports the stall; the duration lands when it ends (ADR-0082 D5).
    /// </summary>
    private async Task StallAsync(Task wait)
    {
        if (wait.IsCompleted)
        {
            await wait.ConfigureAwait(false);
            return;
        }

        long stalls = Interlocked.Increment(ref _producerStalls);
        CameraDiagnostics.ProducerStalls.Add(1);

        // A stalled producer stalls on most frames, so the same throttle the
        // drop log uses: the first one, then every hundredth.
        if (stalls == 1 || stalls % 100 == 0)
            LogProducerStalled(_logger, stalls, Options.QueueDepth, _pool.OutstandingLeases);

        var parked = _timeProvider.GetTimestamp();
        try
        {
            await wait.ConfigureAwait(false);
        }
        finally
        {
            // In the finally so a cancelled stall still reports the time it
            // held the producer off the driver. That time is lost frames
            // whether or not the wait ended in a frame.
            var elapsed = _timeProvider.GetElapsedTime(parked);
            Interlocked.Add(ref _producerStallTicks, elapsed.Ticks);
            CameraDiagnostics.ProducerStallDuration.Record(elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// The delivery channel's eviction hook — the entire
    /// <see cref="BufferExhaustionPolicy.LatestWins"/> mechanism. Runs on the
    /// producer's own thread, outside the channel lock.
    /// </summary>
    private void OnFrameEvicted(LeasedCameraFrame frame)
    {
        // Disposing is what returns the pooled buffer. Without it the pool loses
        // one buffer per drop and the session stops delivering after BufferCount
        // of them.
        frame.Dispose();
        CountDrop();
    }

    private void CountDrop()
    {
        _pool.IncrementDropped();

        // Under LatestWins a consumer that is merely slow drops on most frames,
        // so a Warning per drop is its own incident. First one, then every
        // hundredth: enough to see it start and to see it continue.
        long dropped = _pool.FramesDropped;
        if (dropped == 1 || dropped % 100 == 0)
            LogFrameDropped(_logger, dropped, Options.ExhaustionPolicy, _pool.OutstandingLeases);
    }

    private async Task StopProducerAsync()
    {
        // _stopLock serialises this method against (a) the other stop callers —
        // CaptureAsync's finally (producer-driven), DisposeAsync and
        // StopCaptureAsync (caller-driven) — and (b) producer *start* in
        // StartProducerAsync. Serialising against start is what makes the
        // Interlocked.Exchange-to-null below safe: a start can never be half-way
        // through publishing the producer fields while we null them. Among the
        // stop callers, the first through Exchanges the live references and tears
        // them down; the rest see nulls and skip (idempotent, crash-free).
        await _stopLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Atomic claim under the lock: take ownership of each producer
            // field by exchanging it to null. The first caller through walks
            // away with the live references; subsequent callers see nulls and
            // skip — idempotent and crash-free. (The lock alone is enough for
            // correctness because we're serialised, but the Exchange-to-null
            // makes the cleanup self-documenting and survives future
            // refactors that might add concurrent reads.)
            var producerCts  = Interlocked.Exchange(ref _producerCts,  null);
            var producerTask = Interlocked.Exchange(ref _producerTask, null);
            var channel      = Interlocked.Exchange(ref _channel,      null);

            if (producerCts is not null)
            {
                producerCts.Cancel();

                // Tell the backend to stop streaming *before* waiting on the
                // producer task. The producer's IO call (e.g. IMFSourceReader::ReadSample
                // on Windows) is synchronous and does not observe the producer's
                // CancellationToken — only a backend-level Flush / Stop can unblock
                // it. Reversing this order causes the producer to wait forever on
                // its blocking IO while we wait forever on the producer, which
                // also prevents downstream cleanup (Shutdown / Release on the MF
                // source) from running and leaves the camera wedged for any
                // subsequent open. Symptom: subsequent captures fail with
                // MF_E_HW_MFT_FAILED_START_STREAMING (0xC00D3704) until the
                // camera is replugged.
                // Bound the backend stop. We've seen wedged USB-camera drivers
                // (NexiGo MJPEG @1280x720 30fps after ~20 frames) where
                // IMFSourceReader::Flush itself blocks indefinitely. Run on a
                // background task with a timeout so disposal can't hang here.
                await RunBoundedAsync(_backend.StopCaptureAsync, TimeSpan.FromSeconds(2),
                    "backend.StopCaptureAsync").ConfigureAwait(false);

                if (producerTask is not null)
                {
                    // After Flush, the producer's blocking IO should observe
                    // cancellation on its next iteration. If the driver wedge
                    // also blocks Flush from completing (or Flush returns but
                    // doesn't actually interrupt ReadSample), the producer
                    // task may stay running. Bound the wait; the backend
                    // disposal below will Shutdown the source, which is the
                    // nuclear option, and the producer task is a thread-pool
                    // background task that won't prevent process exit.
                    var producerDone = await Task.WhenAny(
                        producerTask,
                        Task.Delay(TimeSpan.FromSeconds(2), _timeProvider)).ConfigureAwait(false);

                    if (producerDone == producerTask)
                    {
                        try { await producerTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) { /* normal shutdown */ }
                        catch (Exception ex)
                        {
                            // Producer faulted on its way out (e.g. device-lost
                            // racing with our stop). Record for final inspection
                            // but don't re-throw from cleanup — disposal must
                            // not propagate transient shutdown errors.
                            Interlocked.CompareExchange(ref _captureFault, ex, null);
                        }
                    }
                    // else: abandoned. The next backend.DisposeAsync call will
                    // Shutdown the MF source and unblock it.
                }
                producerCts.Dispose();
            }

            if (channel is not null)
            {
                while (channel.Reader.TryRead(out var frame))
                    frame.Dispose();
            }
        }
        finally
        {
            _stopLock.Release();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void RecordFrame(TimeSpan timestamp)
    {
        // Single increment site: bump the field, the Meter, and check
        // periodic-status cadence — see logging-and-diagnostics.md §7.
        var produced = Interlocked.Increment(ref _framesProduced);
        CameraDiagnostics.FramesProduced.Add(1);
        _lastTimestamp = timestamp;

        // Bounded item rate (camera fps), so count-gating is appropriate.
        if (produced % 100 == 0)
        {
            LogPeriodicStatus(_logger, produced, _pool.FramesDropped, _pool.OutstandingLeases);
        }
    }

    private void ClaimMode(int mode)
    {
        if (Interlocked.CompareExchange(ref _captureMode, mode, 0) != 0)
            throw new InvalidOperationException(
                "Capture is already active on this session. Stop the current capture before starting a new one.");
    }

    private void ReleaseMode() => Volatile.Write(ref _captureMode, 0);

    // Pool seed size. Routes through the one pure source of truth so the per-format
    // cost can never drift from the backends/pool again (review findings 2.1 + 6.3).
    private static int EstimateFrameSize(CameraFormat format) =>
        CameraFrameLayout.FrameSize(format.PixelFormat, format.Width, format.Height);

    // ── Disposal ───────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsCapturing)
        {
            await StopProducerAsync().ConfigureAwait(false);
            ReleaseMode();
        }

        Device.OnSessionDisposed();

        if (_ownsDevice)
            await Device.DisposeAsync().ConfigureAwait(false);

        // _stopLock is intentionally NOT disposed. Producer start
        // (StartProducerAsync) and the stop paths both wait on it, and a
        // producer-driven StopProducerAsync (CaptureAsync's finally) can still be
        // racing toward WaitAsync() at this instant — the `_disposed` flag does
        // not guard that wait, and a check-then-dispose would only narrow the
        // TOCTOU window. Disposing here throws ObjectDisposedException on that
        // path (the flaky StopProducerRaceTests.ConcurrentDispose failure, CI run
        // 27167571074). SemaphoreSlim allocates a disposable wait handle only if
        // its AvailableWaitHandle is read; this type only ever calls WaitAsync()/
        // Release(), so it owns no unmanaged/IDisposable resource and letting the
        // GC reclaim it leaks nothing.
    }

    /// <summary>
    /// Runs a backend cleanup operation with a hard timeout. Wedged USB
    /// camera drivers can block COM calls (Flush, Shutdown, even Release)
    /// indefinitely; this lets disposal proceed regardless. Any abandoned
    /// work continues on a thread-pool background thread that won't prevent
    /// process exit.
    /// </summary>
    /// <remarks>
    /// The timeout race is measured over the session's injected
    /// <see cref="_timeProvider"/> so disposal's bounded waits are driven by
    /// the same clock as the rest of the session (review finding 2.2); under
    /// <see cref="TimeProvider.System"/> the behaviour is unchanged.
    /// </remarks>
    private async Task RunBoundedAsync(Func<Task> work, TimeSpan timeout, string label)
    {
        var task = Task.Run(work);
        var winner = await Task.WhenAny(task, Task.Delay(timeout, _timeProvider)).ConfigureAwait(false);
        if (winner != task)
        {
            // Background task continues, but disposal proceeds. Don't await
            // its result — this is the whole point.
            Console.Error.WriteLine(
                $"WARNING: {label} did not complete within {timeout.TotalSeconds:F1}s; abandoning.");
        }
        else
        {
            try { await task.ConfigureAwait(false); }
            catch { /* swallowed; cleanup-time errors aren't actionable */ }
        }
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Producer started on backend {BackendType} with {BufferCount} buffers")]
    private static partial void LogProducerStarted(
        ILogger logger, string backendType, int bufferCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Producer stopped: produced={FramesProduced} dropped={FramesDropped} "
            + "stalls={ProducerStalls} stalled={StalledSec:F2}s duration={DurationSec:F2}s")]
    private static partial void LogProducerStopped(
        ILogger logger, long framesProduced, long framesDropped,
        long producerStalls, double stalledSec, double durationSec);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Backend producer faulted on {NativeEndpoint}")]
    private static partial void LogProducerFaulted(
        ILogger logger, string nativeEndpoint, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Frame dropped (#{DroppedCount}); pipeline full under {Policy}. Outstanding={OutstandingLeases}")]
    private static partial void LogFrameDropped(
        ILogger logger, long droppedCount, BufferExhaustionPolicy policy, int outstandingLeases);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Producer stalled (#{StallCount}); consumer has not drained a queue of {QueueDepth}. "
            + "The driver is discarding frames uncounted while this holds. Outstanding={OutstandingLeases}")]
    private static partial void LogProducerStalled(
        ILogger logger, long stallCount, int queueDepth, int outstandingLeases);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Periodic status: produced={FramesProduced} dropped={FramesDropped} outstanding={OutstandingLeases}")]
    private static partial void LogPeriodicStatus(
        ILogger logger, long framesProduced, long framesDropped, int outstandingLeases);
}
