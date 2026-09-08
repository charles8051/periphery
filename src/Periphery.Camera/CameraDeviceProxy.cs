// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Camera;

/// <summary>
/// Reconnect-resilient camera handle. Binds a camera by identity, opens a
/// configured <see cref="CameraSession"/> whenever the tracked device becomes
/// active, pumps frames to a consumer callback, and disposes the session on
/// every exit path — reopening after a replug without consumer involvement.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle owner <c>Periphery.Camera</c> was missing (ADR-0084 D5, issue
/// #139). <c>Periphery.Usb</c>, <c>Periphery.Hid</c> and <c>Periphery.Monitor</c>
/// each ship one; camera did not, so a consumer wanting a camera bound by
/// identity wrote the tracker subscription, the open, the frame pump, the
/// disposal-on-every-exit-path and the restart by hand. All of that lives in
/// <see cref="DeviceProxyBase{TDevice,TException}"/>, which this type supplies
/// two hooks to: <see cref="OpenDeviceAsync"/> opens the session, and
/// <see cref="WhileOpenAsync"/> is the frame pump.
/// </para>
/// <para>
/// <b>The device type is the session, not the device.</b>
/// <see cref="CameraSession"/> owns the <see cref="CameraDevice"/> it was opened
/// from, so disposing the session — which the base does on every close, fault and
/// dispose path — releases the whole chain. A proxy over <see cref="CameraDevice"/>
/// would leave the session as a second thing to own.
/// </para>
/// <para>
/// <b>One frame at a time, and no opinion about what happens next.</b> The pump
/// hands the consumer each frame and disposes it when the callback returns. This
/// is deliberately not a router or a fan-out primitive — that scope was settled
/// against in issue #121 and is unchanged here. A consumer that needs a frame to
/// outlive its callback calls <see cref="LeasedCameraFrame.AddRef"/> and owns the
/// reference it gets back; anything else is application policy.
/// </para>
/// <para>
/// <b>A pooled buffer stays held for at least as long as the callback runs, and
/// that is what sizes the pool.</b> The callback's own lease lasts exactly as long
/// as <c>onFrame</c> does. A reference taken with
/// <see cref="LeasedCameraFrame.AddRef"/> keeps the <em>same</em> pooled buffer
/// leased past the return, until that reference is disposed too — so retaining a
/// frame extends occupancy rather than escaping it. Either way the buffer is
/// unavailable to the producer for the whole time, so the pool has to be budgeted
/// against how long consumers hold frames rather than against frame rate alone.
/// Work done inline — a pixel-format conversion, a scale, an encode, a large copy
/// — and work done on a retained reference both count.
/// <see cref="CameraSessionOptions.BufferCount"/> is how many frames a consumer
/// may hold at once; with a slow callback, or with frames retained and fanned out
/// to several consumers, the default of three is reached quickly and the rest are
/// dropped under
/// <see cref="CameraSessionOptions.ExhaustionPolicy"/>. That is arithmetic, not a
/// fault, and the drops are counted in
/// <see cref="CameraSessionMetrics.FramesDropped"/>. Size the pool through the
/// <c>configure</c> hook, or return from the callback promptly and do the slow
/// work on a frame that owns its memory — <see cref="LeasedCameraFrame.Copy"/> is
/// the escape from the pool, where <see cref="LeasedCameraFrame.AddRef"/> only
/// extends a lease on it.
/// </para>
/// <para>
/// <b>A dead stream is an ordinary fault.</b> A wedged camera stays enumerated
/// and active while delivering nothing, so no PnP edge fires and a tracker-driven
/// reopen never happens. <see cref="CameraCaptureOptions.FrameTimeout"/> — five
/// seconds by default — turns that into <see cref="CameraTimeoutException"/> out
/// of the pump, which the base recovers through the same
/// <see cref="IRecoveryPolicy"/> ladder as any other fault.
/// </para>
/// <para>
/// <b>Reopening is safe against an abandoned teardown.</b> A stalled session's
/// teardown can overrun its budget and keep running while still holding the
/// device. A reopen into that used to contend and fail in a way that read as a
/// stream fault; it now fails fast with
/// <see cref="CameraTeardownPendingException"/>, which the recovery ladder treats
/// as any other open failure and retries on its own cadence (issue #123). The
/// refusal is bounded, so a device whose teardown never returns is not refused
/// for the life of the process.
/// </para>
/// <para>
/// <b>The reset rung needs no camera-specific mechanism.</b> Issue #123 asked
/// whether a wedged camera wants its own <see cref="IDeviceReset"/>, on the
/// <c>TreehopperDeviceReset</c> precedent. It does not, and the difference is
/// what a reset would have to speak. Treehopper's extra rungs are device-protocol
/// verbs — a reboot opcode and an EP0 vendor request — that only that firmware
/// defines. UVC standardises no reset request, so there is no gentler,
/// camera-specific rung to write. What clears a wedged UVC driver is a USB port
/// cycle or a PnP disable/enable, the mechanised form of the replug #123
/// recommends, and <see cref="DeviceReset.PlatformDefault"/> already advertises
/// both for a USB-backed camera. This proxy reaches them with no camera code: the
/// escalation is gated by the policy, so pass an
/// <see cref="EscalatingResetRecoveryPolicy"/> (or any policy returning
/// <see cref="RecoveryDirective.Reset"/>) as <c>recoveryPolicy</c> to walk the
/// ladder instead of retrying forever. The default backoff policy never resets,
/// so this stays opt-in.
/// </para>
/// </remarks>
public sealed class CameraDeviceProxy : DeviceProxyBase<CameraSession, CameraException>
{
    private readonly Func<LeasedCameraFrame, CancellationToken, Task> _onFrame;
    private readonly Action<CameraSessionBuilder>? _configure;
    private readonly CameraCaptureOptions? _captureOptions;

    private CameraDeviceProxy(
        DeviceTracker tracker,
        DeviceWatcher watcher,
        Func<LeasedCameraFrame, CancellationToken, Task> onFrame,
        Action<CameraSessionBuilder>? configure,
        CameraCaptureOptions? captureOptions,
        IRecoveryPolicy? recoveryPolicy)
        : base(tracker, watcher, recoveryPolicy)
    {
        _onFrame = onFrame;
        _configure = configure;
        _captureOptions = captureOptions;
    }

    private CameraDeviceProxy(
        DeviceTracker tracker,
        Func<LeasedCameraFrame, CancellationToken, Task> onFrame,
        Action<CameraSessionBuilder>? configure,
        CameraCaptureOptions? captureOptions,
        IRecoveryPolicy? recoveryPolicy)
        : base(tracker, recoveryPolicy)
    {
        _onFrame = onFrame;
        _configure = configure;
        _captureOptions = captureOptions;
    }

    /// <summary>
    /// Opens a capture-ready session on the newly active device, applying the
    /// caller's format and option choices to a fresh builder.
    /// </summary>
    /// <remarks>
    /// The builder is rebuilt per connection rather than captured once, because
    /// format selection reads a snapshot of the device that just arrived. A
    /// replugged camera can enumerate different formats than the one it replaced,
    /// and reusing a resolved <see cref="CameraConfiguration"/> would reapply a
    /// format the new device may not advertise.
    /// </remarks>
    protected override Task<CameraSession> OpenDeviceAsync(DeviceInfo deviceInfo, CancellationToken ct)
    {
        var builder = CameraSession.For(deviceInfo);
        _configure?.Invoke(builder);
        return builder.OpenAsync(ct);
    }

    /// <inheritdoc />
    protected override bool HasWorker => true;

    /// <summary>
    /// The frame pump. Runs for as long as the session is open, handing each
    /// frame to the consumer callback and disposing it afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each frame is disposed once the callback returns, which returns its buffer
    /// to the pool. A callback that needs the frame afterwards must
    /// <see cref="LeasedCameraFrame.AddRef"/> it and dispose the reference it
    /// receives; holding the original past return is a use-after-release bug the
    /// pool will report.
    /// </para>
    /// <para>
    /// A fault here — a device lost mid-capture, or a
    /// <see cref="CameraTimeoutException"/> from a stream that stopped delivering
    /// — propagates, and the base closes the session and runs the recovery ladder.
    /// Cancellation is the ordinary shutdown path and closes nothing extra.
    /// </para>
    /// </remarks>
    protected override async Task WhileOpenAsync(CameraSession session, CancellationToken ct)
    {
        await foreach (var frame in session.CaptureAsync(_captureOptions, ct).ConfigureAwait(false))
        {
            using (frame)
            {
                await _onFrame(frame, ct).ConfigureAwait(false);
            }
        }

        // Falling out without cancellation means the producer ended while the
        // session was still nominally open — the channel completed with no fault
        // to raise. The base treats a normal return as "leave the device open",
        // which would park a live handle behind a stream that is never going to
        // deliver again. Faulting instead puts it through the recovery ladder.
        ct.ThrowIfCancellationRequested();
        throw new CameraException(
            "The capture stream ended while the camera was still open. The session is no longer " +
            "producing frames, so it is being recycled through the recovery policy.",
            session.DeviceInfo.Id);
    }

    /// <summary>
    /// Creates a self-contained proxy that owns its watcher and starts tracking
    /// cameras matching <paramref name="profile"/>.
    /// </summary>
    /// <param name="profile">Identity of the camera to bind.</param>
    /// <param name="onFrame">
    /// Invoked for each delivered frame. The frame is disposed when this returns;
    /// call <see cref="LeasedCameraFrame.AddRef"/> to retain one beyond the call.
    /// A throw from here faults the pump and triggers recovery. <b>A pooled buffer
    /// is held for at least as long as this runs</b>, and longer still if the
    /// callback retains the frame — see the pool-sizing note on
    /// <see cref="CameraDeviceProxy"/> before doing conversion or encode work
    /// inline.
    /// </param>
    /// <param name="configure">
    /// Optional format and session configuration, applied to a fresh builder on
    /// every connection.
    /// </param>
    /// <param name="captureOptions">
    /// Optional per-capture options. Leave null for the default five-second
    /// <see cref="CameraCaptureOptions.FrameTimeout"/>, which is what turns a
    /// wedged stream into a recoverable fault.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Optional reconnect-cadence/give-up policy. Defaults to
    /// <see cref="ExponentialBackoffRecoveryPolicy.Default"/>.
    /// </param>
    /// <param name="ct">Cancellation token for the initial watcher start.</param>
    /// <remarks>
    /// Deliberately <c>async</c> rather than returning the helper's task
    /// directly. The other proxies' <c>OpenAsync</c> factories validate only
    /// <paramref name="profile"/>, which the shared helper checks from inside an
    /// async method — so a null argument reaches the caller as a faulted task.
    /// This one has a second required argument to check before construction, and
    /// checking it in a non-async method would throw synchronously instead,
    /// making the one camera factory behave differently from the other four for
    /// the same mistake. <c>DeviceProxy&lt;TDevice&gt;.OpenAsync</c> is async for
    /// exactly this reason.
    /// </remarks>
    public static async Task<CameraDeviceProxy> OpenAsync(
        DeviceProfile profile,
        Func<LeasedCameraFrame, CancellationToken, Task> onFrame,
        Action<CameraSessionBuilder>? configure = null,
        CameraCaptureOptions? captureOptions = null,
        IRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(onFrame);

        return await OpenWithOwnedWatcherAsync(
            profile,
            (tracker, watcher) => new CameraDeviceProxy(
                tracker, watcher, onFrame, configure, captureOptions, recoveryPolicy),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a proxy that borrows a caller-owned <paramref name="tracker"/>
    /// already attached to a running watcher. Use when one watcher powers
    /// several devices.
    /// </summary>
    /// <param name="tracker">A tracker already attached to a running watcher.</param>
    /// <param name="onFrame">
    /// Invoked for each delivered frame. The frame is disposed when this returns,
    /// and holds a pooled buffer for at least as long as it runs — longer if the
    /// callback retains it.
    /// </param>
    /// <param name="configure">Optional per-connection session configuration.</param>
    /// <param name="captureOptions">Optional per-capture options.</param>
    /// <param name="recoveryPolicy">Optional reconnect-cadence/give-up policy.</param>
    public static CameraDeviceProxy Create(
        DeviceTracker tracker,
        Func<LeasedCameraFrame, CancellationToken, Task> onFrame,
        Action<CameraSessionBuilder>? configure = null,
        CameraCaptureOptions? captureOptions = null,
        IRecoveryPolicy? recoveryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(onFrame);

        return CreateWithBorrowedTracker(
            tracker,
            t => new CameraDeviceProxy(t, onFrame, configure, captureOptions, recoveryPolicy));
    }
}
