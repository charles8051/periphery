// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Camera.Internal;

namespace Periphery.Camera.Linux;

/// <summary>
/// Linux implementation of <see cref="ICameraBackend"/> over V4L2
/// (<c>/dev/videoN</c>): format/control discovery via the enumeration
/// ioctls, capture via the streaming mmap queue
/// (<c>REQBUFS</c>/<c>QBUF</c>/<c>DQBUF</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReadRawFrameAsync"/> runs synchronously on the caller's thread
/// for the same reason the Media Foundation backend does: the
/// <see cref="CameraSession"/> producer task is LongRunning, so it owns a
/// dedicated thread and a thread-pool hop per frame buys nothing. The wait is
/// <c>poll(2)</c> on the device fd plus an <c>eventfd</c> that cancellation
/// and disposal signal.
/// </para>
/// <para>
/// Frame delivery is zero-copy out of the driver: <c>DQBUF</c> hands back an
/// mmap'd kernel buffer, the <see cref="RawCameraFrame"/> wraps that mapping
/// directly, and the buffer is only re-queued (<c>QBUF</c>) at the <b>next</b>
/// read — exactly the "memory valid until the next
/// <see cref="ICameraBackend.ReadRawFrameAsync"/> call" contract the frame
/// pool copies under.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class V4l2CameraBackend : ICameraBackend
{
    private const int BufferCount = 4;

    private readonly Periphery.DeviceInfo _deviceInfo;
    private string _devNode;
    // Owned handles, not raw ints: see V4l2FileDescriptor for why an fd number cannot be
    // carried across a teardown (#256). BOTH descriptors need it. The wake eventfd looked
    // like it did not — this class creates and closes it and never hands it to a caller —
    // but WaitForFrame reads it on the capture thread while DisposeAsync closes it, which is
    // the same check-then-use gap one field over (#273 review turn 1).
    private V4l2FileDescriptor? _handle;
    private V4l2FileDescriptor? _wakeHandle;
    private CameraConfiguration? _configuration;
    private V4l2Interop.V4l2PixFormat _negotiated;
    private CameraPixelFormat _negotiatedFormat;
    private MmapBuffer[] _buffers = [];
    private int _pendingIndex = -1;
    private bool _isCapturing;
    private volatile bool _disposed;

    internal V4l2CameraBackend(Periphery.DeviceInfo deviceInfo)
    {
        _deviceInfo = deviceInfo;
        _devNode = deviceInfo.Id;
    }

    public string NativeEndpointId => _devNode;

    // -----------------------------------------------------------------------
    // Open
    // -----------------------------------------------------------------------

    public Task OpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            string devNode = ResolveDevNode(_deviceInfo.Id);
            int fd = V4l2Interop.Open(
                devNode,
                V4l2Interop.O_RDWR | V4l2Interop.O_NONBLOCK | V4l2Interop.O_CLOEXEC);

            if (fd < 0)
                throw MapOpenError(Marshal.GetLastPInvokeError(), devNode);

            // Wrapped the instant it exists, so the descriptor is owned for its whole life
            // rather than from the end of a successful open — the capability probe below is
            // already an ioctl, and it should go through the same handle everything else does.
            var handle = new V4l2FileDescriptor(fd);
            try
            {
                EnsureCaptureCapable(handle, devNode);

                int wakeFd = V4l2Interop.EventFd(
                    0, V4l2Interop.EFD_NONBLOCK | V4l2Interop.EFD_CLOEXEC);
                if (wakeFd < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new CameraException(
                        $"Failed to create wake eventfd for '{devNode}'. errno: {errno}",
                        new IOException($"eventfd() failed. errno: {errno}"), _deviceInfo.Id);
                }

                _devNode = devNode;
                _handle = handle;
                _wakeHandle = new V4l2FileDescriptor(wakeFd);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }, ct);
    }

    private CameraException MapOpenError(int errno, string devNode)
    {
        var inner = new IOException($"open('{devNode}') failed. errno: {errno}");
        return errno switch
        {
            V4l2Interop.EACCES or V4l2Interop.EPERM =>
                new CameraAccessDeniedException(
                    $"Access denied opening camera '{_deviceInfo.Id}' ({devNode}). "
                    + "The calling user lacks read/write permission on the V4L2 node — "
                    + "join the 'video' group or add a udev rule.",
                    inner, _deviceInfo.Id),
            V4l2Interop.EBUSY =>
                new CameraAccessDeniedException(
                    $"Camera '{_deviceInfo.Id}' ({devNode}) is busy — "
                    + "another process holds it open.",
                    inner, _deviceInfo.Id),
            V4l2Interop.ENOENT or V4l2Interop.ENODEV or V4l2Interop.ENXIO =>
                new CameraDeviceNotFoundException(
                    $"Camera '{_deviceInfo.Id}' was not found at {devNode}. "
                    + "It may have been unplugged between enumeration and open.",
                    inner, _deviceInfo.Id),
            _ =>
                new CameraException(
                    $"Failed to open camera '{_deviceInfo.Id}' ({devNode}). errno: {errno}",
                    inner, _deviceInfo.Id),
        };
    }

    private unsafe void EnsureCaptureCapable(V4l2FileDescriptor fd, string devNode)
    {
        var caps = new V4l2Interop.V4l2Capability();
        if (V4l2Interop.IoctlRetry(fd, V4l2Interop.VIDIOC_QUERYCAP, &caps) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new CameraException(
                $"'{devNode}' is not a V4L2 device (VIDIOC_QUERYCAP failed, errno {errno}).",
                new IOException($"ioctl(VIDIOC_QUERYCAP) failed. errno: {errno}"), _deviceInfo.Id);
        }

        uint effective = (caps.Capabilities & V4l2Interop.V4L2_CAP_DEVICE_CAPS) != 0
            ? caps.DeviceCaps
            : caps.Capabilities;

        if ((effective & V4l2Interop.V4L2_CAP_VIDEO_CAPTURE) == 0
            || (effective & V4l2Interop.V4L2_CAP_STREAMING) == 0)
        {
            throw new CameraException(
                $"'{devNode}' does not support streaming video capture "
                + $"(capabilities: 0x{effective:X8}). It may be a metadata or output node.",
                _deviceInfo.Id);
        }
    }

    // -----------------------------------------------------------------------
    // Formats
    // -----------------------------------------------------------------------

    public Task<IReadOnlyList<CameraFormat>> GetFormatsAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();

        return Task.Run<IReadOnlyList<CameraFormat>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            var formats = new List<CameraFormat>();
            foreach (uint fourcc in EnumeratePixelFormats())
            {
                if (!V4l2FormatMap.TryMapPixelFormat(fourcc, out var pixelFormat))
                    continue; // No neutral representation — skip.

                var transport = pixelFormat == CameraPixelFormat.Mjpeg
                    ? CameraTransport.Compressed
                    : CameraTransport.Uncompressed;

                foreach ((int width, int height) in EnumerateFrameSizes(fourcc))
                {
                    (Rational min, Rational max) = EnumerateFrameRateRange(fourcc, width, height);
                    formats.Add(new CameraFormat(width, height, pixelFormat, min, max, transport));
                }
            }
            return formats;
        }, ct);
    }

    private unsafe List<uint> EnumeratePixelFormats()
    {
        var result = new List<uint>();
        for (uint index = 0; ; index++)
        {
            var desc = new V4l2Interop.V4l2FmtDesc
            {
                Index = index,
                Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            };
            if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_ENUM_FMT, &desc) < 0)
                break; // EINVAL terminates the enumeration.
            result.Add(desc.PixelFormat);
        }
        return result;
    }

    private unsafe List<(int Width, int Height)> EnumerateFrameSizes(uint fourcc)
    {
        var sizes = new List<(int, int)>();
        for (uint index = 0; ; index++)
        {
            var frm = new V4l2Interop.V4l2FrmSizeEnum { Index = index, PixelFormat = fourcc };
            if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_ENUM_FRAMESIZES, &frm) < 0)
                break;

            if (frm.Type == V4l2Interop.V4L2_FRMSIZE_TYPE_DISCRETE)
            {
                sizes.Add(((int)frm.DiscreteWidth, (int)frm.DiscreteHeight));
                continue;
            }

            // Stepwise / continuous (virtual drivers like v4l2loopback report
            // these): synthesize the common ladder clamped to the advertised
            // range, snapped to the step grid.
            uint stepW = Math.Max(frm.StepWidth, 1);
            uint stepH = Math.Max(frm.StepHeight, 1);
            ReadOnlySpan<(uint W, uint H)> ladder =
            [
                (frm.MinWidth, frm.MinHeight),
                (640, 480), (1280, 720), (1920, 1080),
                (frm.MaxWidth, frm.MaxHeight),
            ];
            foreach ((uint w, uint h) in ladder)
            {
                uint cw = Math.Clamp(w, frm.MinWidth, frm.MaxWidth);
                uint ch = Math.Clamp(h, frm.MinHeight, frm.MaxHeight);
                cw = frm.MinWidth + (cw - frm.MinWidth) / stepW * stepW;
                ch = frm.MinHeight + (ch - frm.MinHeight) / stepH * stepH;
                var size = ((int)cw, (int)ch);
                if (!sizes.Contains(size))
                    sizes.Add(size);
            }
            break; // Range descriptors are single-entry.
        }
        return sizes;
    }

    private unsafe (Rational Min, Rational Max) EnumerateFrameRateRange(uint fourcc, int width, int height)
    {
        // v4l2_fract intervals are seconds-per-frame; rate is the reciprocal.
        Rational? min = null, max = null;
        for (uint index = 0; ; index++)
        {
            var ival = new V4l2Interop.V4l2FrmIvalEnum
            {
                Index = index,
                PixelFormat = fourcc,
                Width = (uint)width,
                Height = (uint)height,
            };
            if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_ENUM_FRAMEINTERVALS, &ival) < 0)
                break;

            if (ival.Type == V4l2Interop.V4L2_FRMIVAL_TYPE_DISCRETE)
            {
                if (ival.Numerator == 0 || ival.Denominator == 0) continue;
                var rate = new Rational((int)ival.Denominator, (int)ival.Numerator);
                if (min is null || rate < min.Value) min = rate;
                if (max is null || rate > max.Value) max = rate;
            }
            else
            {
                // Stepwise/continuous: min interval = max rate and vice versa.
                if (ival.Denominator != 0 && ival.Numerator != 0)
                    max = new Rational((int)ival.Denominator, (int)ival.Numerator);
                if (ival.MaxDenominator != 0 && ival.MaxNumerator != 0)
                    min = new Rational((int)ival.MaxDenominator, (int)ival.MaxNumerator);
                break;
            }
        }

        // Drivers that don't implement interval enumeration get a sane default.
        var fallback = new Rational(30, 1);
        return (min ?? fallback, max ?? min ?? fallback);
    }

    // -----------------------------------------------------------------------
    // Controls
    // -----------------------------------------------------------------------

    public Task<IReadOnlyList<CameraControlInfo>> GetControlsAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();

        return Task.Run<IReadOnlyList<CameraControlInfo>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            var controls = new List<CameraControlInfo>();
            foreach (var kind in V4l2FormatMap.EnumerableControlKinds)
            {
                if (!V4l2FormatMap.TryGetControlId(kind, out uint id, out uint autoId))
                    continue;
                if (!TryQueryControl(id, out var query))
                    continue;

                bool supportsAuto = autoId != 0 && TryQueryControl(autoId, out _);
                controls.Add(new CameraControlInfo(
                    kind,
                    kind.ToString(),
                    query.Minimum,
                    query.Maximum,
                    query.Step,
                    query.DefaultValue,
                    supportsAuto,
                    (query.Flags & V4l2Interop.V4L2_CTRL_FLAG_READ_ONLY) != 0));
            }
            return controls;
        }, ct);
    }

    /// <summary>
    /// Whether the device has a control, kept distinct from whether we could ask.
    /// </summary>
    /// <remarks>
    /// Collapsing these two is how the mode-enforcement hole reopened one level up
    /// from where it was closed: a guard written as "does the companion exist?"
    /// silently becomes "did the query succeed?", and a transient driver error then
    /// skips the enforcement entirely, letting a write land while the auto loop
    /// still owns the control. Same distinction as
    /// <see cref="CameraControlMode.Unknown"/>, one layer down.
    /// </remarks>
    private enum ControlPresence
    {
        /// <summary>The device answered and does not have it. A real determination.</summary>
        Absent,

        /// <summary>The device answered and has it.</summary>
        Present,

        /// <summary>The device did not answer. Nothing may be concluded.</summary>
        Unreadable,
    }

    private unsafe ControlPresence QueryControl(
        uint id, out V4l2Interop.V4l2QueryCtrl query, out int errno)
    {
        query = default;
        errno = 0;

        var q = new V4l2Interop.V4l2QueryCtrl { Id = id };
        if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_QUERYCTRL, &q) < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            // EINVAL is V4L2's "no such control" and is an answer. Anything else
            // — ENODEV on a yanked USB camera, EIO, EACCES — means the question
            // did not get through.
            return errno == V4l2Interop.EINVAL
                ? ControlPresence.Absent
                : ControlPresence.Unreadable;
        }

        // DISABLED is the device saying the control is present but unusable in its
        // current state, which for every caller here is indistinguishable from not
        // having it.
        if ((q.Flags & V4l2Interop.V4L2_CTRL_FLAG_DISABLED) != 0)
            return ControlPresence.Absent;

        query = q;
        return ControlPresence.Present;
    }

    /// <summary>
    /// Presence only, for the enumeration pass, where listing is best-effort by
    /// nature and an unreadable control is simply one that does not get listed.
    /// </summary>
    private bool TryQueryControl(uint id, out V4l2Interop.V4l2QueryCtrl query) =>
        QueryControl(id, out query, out _) == ControlPresence.Present;

    /// <summary>
    /// Resolve the auto companion for a control operation, throwing when the device
    /// will not say whether it has one and <paramref name="mustKnow"/> — because a
    /// caller that is promising a mode cannot keep that promise over a companion it
    /// could not ask about.
    /// </summary>
    private bool CompanionIsPresent(CameraControlKind control, uint autoId, bool mustKnow)
    {
        if (autoId == 0)
            return false;

        switch (QueryControl(autoId, out _, out int errno))
        {
            case ControlPresence.Present:
                return true;
            case ControlPresence.Absent:
                return false;
            default:
                if (!mustKnow)
                    return false;
                throw new CameraException(
                    $"Could not determine whether control {control} on '{_devNode}' has an "
                    + $"automatic mode, so it cannot be taken off automatic. errno: {errno}",
                    new IOException($"ioctl(VIDIOC_QUERYCTRL) failed. errno: {errno}"),
                    _deviceInfo.Id);
        }
    }

    public Task<CameraControlState?> GetControlAsync(CameraControlKind control, CancellationToken ct)
    {
        ThrowIfNotOpen();

        return Task.Run<CameraControlState?>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!V4l2FormatMap.TryGetControlId(control, out uint id, out uint autoId))
                return null;

            switch (QueryControl(id, out _, out int queryErrno))
            {
                case ControlPresence.Absent:
                    return null;
                case ControlPresence.Unreadable:
                    throw new CameraException(
                        $"Could not determine whether '{_devNode}' has control {control}. "
                        + $"errno: {queryErrno}",
                        new IOException($"ioctl(VIDIOC_QUERYCTRL) failed. errno: {queryErrno}"),
                        _deviceInfo.Id);
            }

            // Past the enumeration check the control demonstrably EXISTS, so a
            // failed read is an operational failure — device removed, permission
            // lost, driver error — and not the "this camera has no zoom" answer
            // that null is reserved for. Collapsing the two would let a camera
            // that vanished mid-call be reported as simply lacking the control.
            if (!TryGetControlValue(id, out int value, out int errno))
            {
                throw new CameraException(
                    $"Reading control {control} from '{_devNode}' failed. errno: {errno}",
                    new IOException($"ioctl(VIDIOC_G_CTRL) failed. errno: {errno}"), _deviceInfo.Id);
            }

            // No auto companion means the device has no automatic behaviour for
            // this control — it is manual by construction, which is a real
            // determination and not the absence of one (ADR-0077 D2). Note that
            // holds whether the ABSENCE comes from the map having no companion id
            // or from the device answering that it has no such control; only a
            // companion that exists and will not answer is Unknown.
            var mode = CameraControlMode.Manual;
            switch (autoId == 0 ? ControlPresence.Absent : QueryControl(autoId, out _, out _))
            {
                case ControlPresence.Present:
                    mode = TryGetControlValue(autoId, out int autoValue)
                        ? V4l2FormatMap.InterpretAutoValue(control, autoValue)
                        : CameraControlMode.Unknown;
                    break;
                case ControlPresence.Unreadable:
                    mode = CameraControlMode.Unknown;
                    break;
            }

            return new CameraControlState(control, value, mode);
        }, ct);
    }

    private unsafe bool TrySetControlValue(uint id, int value)
    {
        var ctl = new V4l2Interop.V4l2Control { Id = id, Value = value };
        return V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_S_CTRL, &ctl) >= 0;
    }

    private bool TryGetControlValue(uint id, out int value) => TryGetControlValue(id, out value, out _);

    private unsafe bool TryGetControlValue(uint id, out int value, out int errno)
    {
        var ctl = new V4l2Interop.V4l2Control { Id = id };
        if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_G_CTRL, &ctl) < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            value = 0;
            return false;
        }

        errno = 0;
        value = ctl.Value;
        return true;
    }

    /// <summary>
    /// Drive the auto companion to <paramref name="mode"/> and <b>confirm it landed</b>,
    /// throwing if the device would not go there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes ADR-0077 D4 true rather than merely intended. An earlier
    /// version wrote the companion best-effort on the theory that a refused mode
    /// switch would surface as <c>EBUSY</c> on the value write that follows — but
    /// V4L2 drivers are under no obligation to refuse that write. Many accept it
    /// and let the auto loop overwrite the value on the next frame, so the caller
    /// gets a successful <c>SetControlAsync</c> and a control the device is still
    /// driving. That silent-wrong-belief is the exact shape the contract exists to
    /// prevent.
    /// </para>
    /// <para>
    /// A refused write is not automatically a failure, which is why this reads back
    /// rather than throwing outright: a read-only companion, or one already sitting
    /// where we want it, can reject the write with the contract still satisfied.
    /// Ask the device instead of assuming — in either direction. If the read-back
    /// also fails we cannot confirm, and per
    /// <see cref="CameraControlMode.Unknown"/>'s reasoning the unconfirmable case
    /// is reported rather than assumed away.
    /// </para>
    /// </remarks>
    private void EnforceCompanionMode(CameraControlKind control, uint autoId, CameraControlMode mode)
    {
        // Try every value that would produce this mode, not just the preferred one. On a menu
        // companion the device need not advertise the entry we would rather use — see
        // V4l2FormatMap.AutoValueCandidates and issue #275, where always writing
        // V4L2_EXPOSURE_AUTO_MODE (0) made ResetControlAsync fail destructively on every UVC
        // camera whose menu offers only MANUAL and APERTURE_PRIORITY.
        int errno = 0;
        foreach (int candidate in V4l2FormatMap.AutoValueCandidates(control, mode))
        {
            // Ask before writing. A menu entry the device does not advertise fails the write
            // with EINVAL, and a failed write here is indistinguishable from a device that
            // refused for some other reason — which is how the destructive path arose.
            if (!SupportsMenuEntry(autoId, candidate))
                continue;

            if (TrySetControlValue(autoId, candidate))
                return;

            errno = Marshal.GetLastPInvokeError();
        }

        // The device may already be there. Read back before failing: a read-only companion, or
        // one already at the requested mode, is not an error.
        if (TryGetControlValue(autoId, out int actual)
            && V4l2FormatMap.InterpretAutoValue(control, actual) == mode)
            return;

        throw new CameraException(
            $"Could not put control {control} into {mode} mode on '{_devNode}': the device "
            + $"advertises none of the companion values that would produce it "
            + $"({string.Join(", ", V4l2FormatMap.AutoValueCandidates(control, mode))}), or "
            + $"refused the write and did not report {mode} when asked. errno: {errno}",
            new IOException($"ioctl(VIDIOC_S_CTRL) failed on the auto companion. errno: {errno}"),
            _deviceInfo.Id);
    }

    /// <summary>
    /// Whether <paramref name="controlId"/> advertises menu entry <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// Non-menu controls have no entries to enumerate, and <c>VIDIOC_QUERYMENU</c> answers
    /// <c>EINVAL</c> for them — indistinguishable from "menu without that entry". So a control
    /// whose type is not a menu is reported as supporting whatever was asked, leaving the
    /// boolean companions behaving exactly as before (#275).
    /// </remarks>
    private bool SupportsMenuEntry(uint controlId, int index) =>
        SupportsMenuEntry(Handle, controlId, index);

    /// <summary>
    /// The descriptor-taking form, so this can be exercised without a
    /// <see cref="V4l2CameraBackend"/> instance.
    /// </summary>
    /// <remarks>
    /// <b>Internal purely so a test can reach it, and that is the point.</b> The only fixture that
    /// could drive this through the public control path is a UVC camera, and those tests are
    /// deliberately out of CI (#277) — which would have left the <em>production</em> menu logic
    /// with no automated regression cover at all, only the raw-ioctl wrapper beside it.
    /// <para>
    /// The subtle line is the bounds check below. A menu may be <b>sparse</b>: an index inside
    /// <c>[Minimum, Maximum]</c> need not exist, so passing the bounds proves nothing and the
    /// query is what decides. vivid's generic menu is sparse in exactly that way, which lets a
    /// virtual fixture tell the two apart.
    /// </para>
    /// </remarks>
    internal static unsafe bool SupportsMenuEntry(
        V4l2FileDescriptor handle, uint controlId, int index)
    {
        if (index < 0)
            return false;

        var query = new V4l2Interop.V4l2QueryCtrl { Id = controlId };

        // A control that cannot be described, or that the device reports DISABLED, is treated as
        // a non-menu — same rule TryQueryControl applies, since for every caller here a disabled
        // control is indistinguishable from an absent one.
        if (V4l2Interop.IoctlRetry(handle, V4l2Interop.VIDIOC_QUERYCTRL, &query) < 0
            || (query.Flags & V4l2Interop.V4L2_CTRL_FLAG_DISABLED) != 0
            || query.Type != V4l2Interop.V4L2_CTRL_TYPE_MENU)
        {
            return true;
        }

        if (index < query.Minimum || index > query.Maximum)
            return false;

        var menu = new V4l2Interop.V4l2QueryMenu { Id = controlId, Index = (uint)index };
        return V4l2Interop.IoctlRetry(handle, V4l2Interop.VIDIOC_QUERYMENU, &menu) >= 0;
    }

    public Task SetControlAsync(CameraControlKind control, double value, CancellationToken ct)
    {
        ThrowIfNotOpen();

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            SetControlCore(control, (int)Math.Round(value), requireManual: true);
        }, ct);
    }

    public Task ResetControlAsync(CameraControlKind control, CancellationToken ct)
    {
        ThrowIfNotOpen();

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!V4l2FormatMap.TryGetControlId(control, out uint id, out uint autoId)
                || !TryQueryControl(id, out var query))
            {
                throw new CameraException(
                    $"Control {control} is not supported by '{_devNode}'.", _deviceInfo.Id);
            }
            // requireManual is FALSE here, unlike the Set path. Forcing manual is
            // only a means to get the default value written; the end state reset
            // promises is Automatic, and that is enforced below. A companion that
            // is stuck automatic — read-only, say — should not fail a reset whose
            // destination it is already at.
            SetControlCore(control, query.DefaultValue, requireManual: false);

            // Then hand it back to the device, which is what reset means on the
            // Media Foundation side (it resets with MF_CAMERA_FLAGS_AUTO). Order
            // matters: SetControlCore may have just forced the companion to manual,
            // so restoring automatic has to come after it.
            //
            // This one IS enforced: a reset that returns successfully having left
            // the control manual is strictly worse than not resetting at all,
            // because SetControlCore took it off automatic on the way through. If
            // this throws, the control is left at its default value in manual mode
            // — a partial state the exception names rather than conceals.
            if (CompanionIsPresent(control, autoId, mustKnow: true))
                EnforceCompanionMode(control, autoId, CameraControlMode.Automatic);
        }, ct);
    }

    /// <param name="requireManual">
    /// Whether taking the control off automatic is part of what the caller is
    /// promising. True for <see cref="SetControlAsync"/>, whose whole contract is
    /// that the device stops driving the control; false for
    /// <see cref="ResetControlAsync"/>, which only passes through manual on its
    /// way to automatic and enforces that destination itself.
    /// </param>
    private unsafe void SetControlCore(CameraControlKind control, int value, bool requireManual)
    {
        if (!V4l2FormatMap.TryGetControlId(control, out uint id, out uint autoId))
            throw new CameraException(
                $"Control {control} has no V4L2 mapping.", _deviceInfo.Id);

        // Take the control away from the device FIRST. Unlike Media Foundation,
        // where MF_CAMERA_FLAGS_MANUAL rides along on the same call, V4L2 leaves
        // the auto loop owning the control until its companion says otherwise —
        // so a bare value write either fails with EBUSY (which is why the hint
        // below tells the caller to disable auto) or is overwritten on the next
        // frame. Without this, SetControlAsync means "pin" on Windows and "make
        // a suggestion" on Linux.
        if (CompanionIsPresent(control, autoId, mustKnow: requireManual))
        {
            if (requireManual)
                EnforceCompanionMode(control, autoId, CameraControlMode.Manual);
            else
                TrySetControlValue(
                    autoId, V4l2FormatMap.MapModeToAutoValue(control, CameraControlMode.Manual));
        }

        var ctl = new V4l2Interop.V4l2Control { Id = id, Value = value };
        if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_S_CTRL, &ctl) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            string hint = errno switch
            {
                V4l2Interop.EINVAL => "The device does not support this control.",
                V4l2Interop.EBUSY =>
                    "The control is currently driven by its auto mode — disable auto first.",
                _ => $"errno: {errno}",
            };
            throw new CameraException(
                $"Setting control {control} to {value} failed on '{_devNode}'. {hint}",
                new IOException($"ioctl(VIDIOC_S_CTRL) failed. errno: {errno}"), _deviceInfo.Id);
        }
    }

    // -----------------------------------------------------------------------
    // Configure / start / stop
    // -----------------------------------------------------------------------

    public Task ConfigureAsync(CameraConfiguration configuration, CancellationToken ct)
    {
        ThrowIfNotOpen();
        if (_isCapturing)
            throw new InvalidOperationException("Cannot reconfigure while capture is running.");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // A format change invalidates any previously-allocated queue.
            ReleaseBuffersCore();

            var format = configuration.Format;
            if (!V4l2FormatMap.TryMapToFourCc(format.PixelFormat, out uint fourcc))
                throw new CameraException(
                    $"Pixel format {format.PixelFormat} has no V4L2 fourcc mapping.",
                    _deviceInfo.Id);

            unsafe
            {
                var fmt = new V4l2Interop.V4l2Format
                {
                    Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                    Pix = new V4l2Interop.V4l2PixFormat
                    {
                        Width = (uint)format.Width,
                        Height = (uint)format.Height,
                        PixelFormat = fourcc,
                        Field = V4l2Interop.V4L2_FIELD_NONE,
                    },
                };

                if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_S_FMT, &fmt) < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new CameraException(
                        $"Configuring '{_devNode}' to {format.Width}x{format.Height} "
                        + $"{format.PixelFormat} failed. errno: {errno}",
                        new IOException($"ioctl(VIDIOC_S_FMT) failed. errno: {errno}"),
                        _deviceInfo.Id);
                }

                // V4L2 drivers adjust rather than reject; surface a mismatch
                // instead of silently capturing something else.
                if (fmt.Pix.PixelFormat != fourcc
                    || fmt.Pix.Width != (uint)format.Width
                    || fmt.Pix.Height != (uint)format.Height)
                {
                    throw new CameraException(
                        $"'{_devNode}' did not accept {format.Width}x{format.Height} "
                        + $"{format.PixelFormat}; the driver adjusted it to "
                        + $"{fmt.Pix.Width}x{fmt.Pix.Height} "
                        + $"{V4l2FormatMap.FourCcToString(fmt.Pix.PixelFormat)}. "
                        + "Pick a format the device enumerated.",
                        _deviceInfo.Id);
                }

                _negotiated = fmt.Pix;
                _negotiatedFormat = format.PixelFormat;
            }

            if (configuration.TargetFrameRate is { } rate && rate.Numerator > 0)
                TrySetFrameRate(rate);

            _configuration = configuration;
        }, ct);
    }

    /// <summary>
    /// Best-effort frame-rate request: <c>timeperframe</c> is the reciprocal
    /// of the rate, and drivers without <c>V4L2_CAP_TIMEPERFRAME</c> simply
    /// run at their native cadence.
    /// </summary>
    private unsafe void TrySetFrameRate(Rational rate)
    {
        var parm = new V4l2Interop.V4l2StreamParm
        {
            Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            TimePerFrameNumerator = (uint)rate.Denominator,
            TimePerFrameDenominator = (uint)rate.Numerator,
        };
        _ = V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_S_PARM, &parm);
    }

    public Task StartCaptureAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();
        if (_configuration is null)
            throw new InvalidOperationException("Device not configured.");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            unsafe
            {
                if (_buffers.Length == 0)
                    AllocateBuffers();

                // Every buffer starts on the driver's queue.
                for (uint i = 0; i < _buffers.Length; i++)
                    Enqueue(i);
                _pendingIndex = -1;

                int type = (int)V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                if (V4l2Interop.Ioctl(Handle, V4l2Interop.VIDIOC_STREAMON, ref type) < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new CameraException(
                        $"Starting the stream on '{_devNode}' failed. errno: {errno}",
                        new IOException($"ioctl(VIDIOC_STREAMON) failed. errno: {errno}"),
                        _deviceInfo.Id);
                }
            }

            _isCapturing = true;
        }, ct);
    }

    private unsafe void AllocateBuffers()
    {
        var req = new V4l2Interop.V4l2RequestBuffers
        {
            Count = BufferCount,
            Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            Memory = V4l2Interop.V4L2_MEMORY_MMAP,
        };
        if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_REQBUFS, &req) < 0 || req.Count == 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new CameraException(
                $"Requesting capture buffers on '{_devNode}' failed. errno: {errno}",
                new IOException($"ioctl(VIDIOC_REQBUFS) failed. errno: {errno}"), _deviceInfo.Id);
        }

        var buffers = new MmapBuffer[req.Count];
        try
        {
            for (uint i = 0; i < req.Count; i++)
            {
                var buf = new V4l2Interop.V4l2Buffer
                {
                    Index = i,
                    Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                    Memory = V4l2Interop.V4L2_MEMORY_MMAP,
                };
                if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_QUERYBUF, &buf) < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new CameraException(
                        $"Querying capture buffer {i} on '{_devNode}' failed. errno: {errno}",
                        new IOException($"ioctl(VIDIOC_QUERYBUF) failed. errno: {errno}"),
                        _deviceInfo.Id);
                }

                IntPtr ptr = V4l2Interop.Mmap(
                    IntPtr.Zero, buf.Length,
                    V4l2Interop.PROT_READ | V4l2Interop.PROT_WRITE,
                    V4l2Interop.MAP_SHARED, Handle, (nint)buf.MmapOffset);
                if (ptr == V4l2Interop.MAP_FAILED)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new CameraException(
                        $"mmap of capture buffer {i} on '{_devNode}' failed. errno: {errno}",
                        new IOException($"mmap() failed. errno: {errno}"), _deviceInfo.Id);
                }

                buffers[i] = new MmapBuffer(ptr, (int)buf.Length);
            }
        }
        catch
        {
            foreach (var b in buffers)
                b?.Unmap();
            ResetBufferQueue();
            throw;
        }

        _buffers = buffers;
    }

    private unsafe void Enqueue(uint index)
    {
        var buf = new V4l2Interop.V4l2Buffer
        {
            Index = index,
            Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            Memory = V4l2Interop.V4L2_MEMORY_MMAP,
        };
        if (V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_QBUF, &buf) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw errno == V4l2Interop.ENODEV
                ? new CameraDeviceLostException(
                    $"Camera '{_devNode}' was disconnected.", _deviceInfo.Id)
                : new CameraException(
                    $"Queueing capture buffer {index} on '{_devNode}' failed. errno: {errno}",
                    new IOException($"ioctl(VIDIOC_QBUF) failed. errno: {errno}"), _deviceInfo.Id);
        }
    }

    public Task StopCaptureAsync()
    {
        _isCapturing = false;
        if (_handle is { IsInvalid: false, IsClosed: false })
        {
            int type = (int)V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            _ = V4l2Interop.Ioctl(Handle, V4l2Interop.VIDIOC_STREAMOFF, ref type);
            _pendingIndex = -1; // STREAMOFF reclaims every buffer, queued or dequeued.
        }
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Frame read
    // -----------------------------------------------------------------------

    public Task<RawCameraFrame> ReadRawFrameAsync(CancellationToken ct)
    {
        ThrowIfNotOpen();
        if (!_isCapturing)
            throw new InvalidOperationException("Capture not started.");

        // Synchronous on the caller's thread — see the class remarks.
        try { return Task.FromResult(ReadFrameCore(ct)); }
        catch (Exception ex) { return Task.FromException<RawCameraFrame>(ex); }
    }

    private unsafe RawCameraFrame ReadFrameCore(CancellationToken ct)
    {
        // The third user of the wake fd, and the one that can fire at any moment — including
        // after teardown has closed it. Registering the HANDLE rather than the number means a
        // late callback is refused by marshalling instead of poking whatever now owns that
        // descriptor; waking a device that is already gone is a no-op by definition, so the
        // refusal is swallowed rather than thrown out of someone else's Cancel() (#273 turn 1).
        using var wake = ct.CanBeCanceled
            ? ct.Register(static state =>
            {
                try { V4l2Interop.SignalEventFd((V4l2FileDescriptor)state!); }
                catch (ObjectDisposedException) { }
            }, WakeHandle)
            : default(CancellationTokenRegistration);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(V4l2CameraBackend));

            // The previous frame's buffer goes back to the driver now that
            // the pool has copied out of it.
            if (_pendingIndex >= 0)
            {
                Enqueue((uint)_pendingIndex);
                _pendingIndex = -1;
            }

            var buf = new V4l2Interop.V4l2Buffer
            {
                Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                Memory = V4l2Interop.V4L2_MEMORY_MMAP,
            };
            if (V4l2Interop.Ioctl(Handle, V4l2Interop.VIDIOC_DQBUF, &buf) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                switch (errno)
                {
                    case V4l2Interop.EAGAIN:
                        WaitForFrame(ct);
                        continue;
                    case V4l2Interop.EINTR:
                        continue;
                    case V4l2Interop.ENODEV:
                        throw new CameraDeviceLostException(
                            $"Camera '{_devNode}' was disconnected mid-capture.", _deviceInfo.Id);
                    default:
                        throw new CameraException(
                            $"Dequeueing a frame from '{_devNode}' failed. errno: {errno}",
                            new IOException($"ioctl(VIDIOC_DQBUF) failed. errno: {errno}"),
                            _deviceInfo.Id);
                }
            }

            // A buffer flagged ERROR is corrupt (transient transfer fault):
            // recycle it and wait for the next one.
            if ((buf.Flags & V4l2Interop.V4L2_BUF_FLAG_ERROR) != 0)
            {
                Enqueue(buf.Index);
                continue;
            }

            _pendingIndex = (int)buf.Index;

            int width = (int)_negotiated.Width;
            int height = (int)_negotiated.Height;
            int stride = (int)_negotiated.BytesPerLine;
            int length = (int)buf.BytesUsed;
            if (length <= 0)
                length = (int)_negotiated.SizeImage;

            var planes = PlaneLayout.DescribePlanes(
                _negotiatedFormat, width, height, stride > 0 ? stride : width);

            return new RawCameraFrame
            {
                Data = _buffers[buf.Index].Memory[..length],
                Width = width,
                Height = height,
                PixelFormat = _negotiatedFormat,
                Timestamp = TimeSpan.FromTicks(
                    buf.TimestampSeconds * TimeSpan.TicksPerSecond
                    + buf.TimestampMicroseconds * TimeSpan.TicksPerMicrosecond),
                PlaneCount = planes?.Count ?? 1,
                Planes = planes,
            };
        }
    }

    private unsafe void WaitForFrame(CancellationToken ct)
    {
        var fds = stackalloc V4l2Interop.PollFd[2];
        // struct pollfd carries a bare int, so this is the one site the ref-counts are held
        // by hand rather than by marshalling. BOTH are held: the wait and the drain that
        // follows it must not outlive either descriptor. Released in the finally below.
        var handle = Handle;
        var wake = WakeHandle;
        bool counted = false, wakeCounted = false;
        try
        {
            // Both AddRefs are INSIDE the try, and each is released on its own flag. Acquiring
            // the camera ref and then throwing on the wake ref would otherwise strand the first
            // one — and a stranded ref-count means the SafeHandle can never be disposed, so the
            // teardown this whole change exists to make safe would hang instead (#273 turn 3).
            // DangerousAddRef sets its flag only on success, so the finally below is exact.
            handle.DangerousAddRef(ref counted);
            wake.DangerousAddRef(ref wakeCounted);
            fds[0] = new V4l2Interop.PollFd { Fd = handle.UnsafeFd, Events = V4l2Interop.POLLIN };
            fds[1] = new V4l2Interop.PollFd { Fd = wake.UnsafeFd, Events = V4l2Interop.POLLIN };

            int rc = V4l2Interop.Poll(fds, 2, timeoutMs: -1);
            if (rc < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == V4l2Interop.EINTR) return;
                throw new CameraException(
                    $"Waiting for a frame from '{_devNode}' failed. errno: {errno}",
                    new IOException($"poll() failed. errno: {errno}"), _deviceInfo.Id);
            }

            if ((fds[1].REvents & V4l2Interop.POLLIN) != 0)
            {
                // Cancellation or disposal — drain and let the loop observe it.
                V4l2Interop.DrainEventFd(wake);
                return;
            }

            const short gone = V4l2Interop.POLLERR | V4l2Interop.POLLHUP | V4l2Interop.POLLNVAL;
            if ((fds[0].REvents & gone) != 0)
            {
                // POLLERR alone can also mean "stream not started" on some
                // drivers, but mid-capture with buffers queued it means the
                // device dropped off the bus.
                throw new CameraDeviceLostException(
                    $"Camera '{_devNode}' dropped off the bus while waiting for a frame.",
                    _deviceInfo.Id);
            }
        }
        finally
        {
            if (wakeCounted) wake.DangerousRelease();
            if (counted) handle.DangerousRelease();
        }
    }

    // -----------------------------------------------------------------------
    // Disposal / teardown
    // -----------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_wakeHandle is { IsInvalid: false, IsClosed: false } wake)
            V4l2Interop.SignalEventFd(wake); // Wake any in-flight poll().

        await StopCaptureAsync().ConfigureAwait(false);

        await Task.Run(() =>
        {
            ReleaseBuffersCore();
            // Dispose() waits for any ref-count taken by an in-flight ioctl before the fd is
            // closed, which is the whole point of the handle — a control call racing teardown
            // now either completes against the real device or throws, never lands on a
            // recycled descriptor.
            _handle?.Dispose();
            _handle = null;

            // Same guarantee for the wake fd: Dispose waits for the ref-count WaitForFrame
            // holds across poll() and the drain that follows it.
            _wakeHandle?.Dispose();
            _wakeHandle = null;
        }).ConfigureAwait(false);
    }

    private unsafe void ReleaseBuffersCore()
    {
        if (_buffers.Length == 0) return;

        foreach (var buffer in _buffers)
            buffer.Unmap();
        _buffers = [];
        _pendingIndex = -1;

        ResetBufferQueue();
    }

    private unsafe void ResetBufferQueue()
    {
        // REQBUFS(0) releases the driver-side queue; best-effort by design
        // (the close() teardown reclaims everything anyway).
        var req = new V4l2Interop.V4l2RequestBuffers
        {
            Count = 0,
            Type = V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            Memory = V4l2Interop.V4L2_MEMORY_MMAP,
        };
        _ = V4l2Interop.IoctlRetry(Handle, V4l2Interop.VIDIOC_REQBUFS, &req);
    }

    private void ThrowIfNotOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle is not { IsInvalid: false, IsClosed: false })
            throw new InvalidOperationException("Device not open.");
    }

    /// <summary>
    /// The open descriptor, for interop. Deliberately re-read at each use rather than captured
    /// once: a caller that passed ThrowIfNotOpen on its own thread and then hops to the pool
    /// (every control method does) must not carry a stale reference across that gap. If
    /// teardown won the race this throws, which is the loud outcome #256 exists to produce.
    /// </summary>
    private V4l2FileDescriptor Handle =>
        _handle is { IsInvalid: false, IsClosed: false } h
            ? h
            : throw new ObjectDisposedException(nameof(V4l2CameraBackend));

    /// <summary>The wake eventfd, same contract as <see cref="Handle"/>.</summary>
    private V4l2FileDescriptor WakeHandle =>
        _wakeHandle is { IsInvalid: false, IsClosed: false } h
            ? h
            : throw new ObjectDisposedException(nameof(V4l2CameraBackend));

    // -----------------------------------------------------------------------
    // Device-node resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves an enumeration identity into an openable <c>/dev/videoN</c>
    /// node. The Linux provider's <c>video4linux</c>-subsystem syspaths end
    /// in the node name itself; other shapes fall back to a
    /// <c>video4linux/</c> class-directory scan and finally the sysfs
    /// <c>uevent</c> <c>DEVNAME</c>. <c>/dev/</c> paths pass through.
    /// </summary>
    internal static string ResolveDevNode(string deviceId)
    {
        if (deviceId.StartsWith("/dev/", StringComparison.Ordinal))
            return deviceId;

        // Parity with Windows, where an unresolvable identity surfaces as
        // device-not-found out of the open call rather than a generic error.
        if (!deviceId.StartsWith("/sys/", StringComparison.Ordinal))
            throw new CameraDeviceNotFoundException(
                $"Camera '{deviceId}' was not found — the identity is neither a sysfs "
                + "path nor a /dev/videoN node.", deviceId);

        if (!Directory.Exists(deviceId))
            throw new CameraDeviceNotFoundException(
                $"Camera '{deviceId}' was not found. "
                + "It may have been unplugged between enumeration and open.", deviceId);

        string trimmed = deviceId.TrimEnd('/');
        string name = Path.GetFileName(trimmed);
        if (name.StartsWith("video", StringComparison.Ordinal) && File.Exists("/dev/" + name))
            return "/dev/" + name;

        string classDir = trimmed + "/video4linux";
        if (Directory.Exists(classDir))
        {
            foreach (string child in Directory.EnumerateDirectories(classDir))
            {
                string childName = Path.GetFileName(child);
                if (childName.StartsWith("video", StringComparison.Ordinal))
                    return "/dev/" + childName;
            }
        }

        string uevent = trimmed + "/uevent";
        if (File.Exists(uevent))
        {
            foreach (string line in File.ReadLines(uevent))
            {
                if (line.StartsWith("DEVNAME=", StringComparison.Ordinal))
                    return "/dev/" + line["DEVNAME=".Length..].Trim();
            }
        }

        throw new CameraException(
            $"Could not resolve a V4L2 node for '{deviceId}' — no videoN class entry "
            + "or DEVNAME found under the sysfs path.", deviceId);
    }

    /// <summary>
    /// Wraps one driver-owned mmap'd capture buffer as
    /// <see cref="Memory{T}"/> so <see cref="RawCameraFrame"/> can reference
    /// it without a copy. The mapping's lifetime is owned by the backend
    /// (<see cref="Unmap"/>), not by this manager's <see cref="Dispose"/>.
    /// </summary>
    private sealed unsafe class MmapBuffer(IntPtr ptr, int length) : MemoryManager<byte>
    {
        private IntPtr _ptr = ptr;
        private readonly int _length = length;

        public override Span<byte> GetSpan() => new((void*)_ptr, _length);

        public override MemoryHandle Pin(int elementIndex = 0) =>
            new((byte*)_ptr + elementIndex); // Native memory — already pinned.

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }

        public void Unmap()
        {
            if (_ptr == IntPtr.Zero) return;
            _ = V4l2Interop.Munmap(_ptr, (nuint)_length);
            _ptr = IntPtr.Zero;
        }
    }
}
