using System;
using System.Runtime.InteropServices;
using Periphery.Camera.Linux;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Reads and writes V4L2 controls through the kernel directly, bypassing Periphery's mapping.
/// </summary>
/// <remarks>
/// <b>This exists to be an independent oracle, and that independence is the whole point.</b>
/// <para>
/// The tests it replaced asserted a round trip: write a mode through
/// <c>V4l2FormatMap.AutoValueCandidates</c>, read it back through
/// <c>V4l2FormatMap.InterpretAutoValue</c>, and check the two agreed. Both are Periphery's own
/// mapping, so a mapping that is <em>wrong in a self-consistent way</em> — the boolean rule
/// applied to the exposure menu, say — satisfies that assertion perfectly. The old tests appeared
/// to discriminate only because this particular camera answered the bad write with
/// <c>EACCES</c>; the failure came from the driver, not from any assertion, and on a device that
/// accepted the write they would have passed while reporting the opposite of the truth (#274).
/// </para>
/// <para>
/// Reading the raw value here fixes the ground truth outside the code under test. "The kernel
/// says <c>V4L2_CID_EXPOSURE_AUTO</c> is 0" is a fact about the device, not a restatement of what
/// Periphery believes.
/// </para>
/// <para>
/// V4L2 permits control ioctls on a second descriptor — only streaming is exclusive — so opening
/// the node alongside an active <see cref="CameraDevice"/> is legitimate.
/// </para>
/// <para>
/// <b>Two families of operation, deliberately.</b> <see cref="Read"/> asserts, and belongs in a
/// test body where a failure to establish ground truth <em>should</em> stop the test. The
/// <c>Try</c> forms never assert and never throw, because their caller is fixture restoration
/// running in a <c>finally</c>: an exception raised there replaces the failure that actually
/// caused the red and hides why (#280 review turn 1).
/// </para>
/// </remarks>
internal static class RawV4l2
{
    /// <summary>Opens the node without asserting. Returns null and the errno on failure.</summary>
    private static V4l2FileDescriptor? TryOpen(string devNode, out int errno)
    {
        int raw = V4l2Interop.Open(
            devNode, V4l2Interop.O_RDWR | V4l2Interop.O_NONBLOCK | V4l2Interop.O_CLOEXEC);

        if (raw < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }

        errno = 0;
        return new V4l2FileDescriptor(raw);
    }

    /// <summary>Reads a control's raw kernel value. Fails the test if it cannot.</summary>
    /// <remarks>
    /// Asserting on purpose: this is the oracle, and a test that cannot read ground truth must not
    /// quietly fall back to asserting Periphery against itself. Never call it from a
    /// <c>finally</c>.
    /// </remarks>
    internal static int Read(string devNode, uint controlId)
    {
        Assert.True(TryRead(devNode, controlId, out int value, out int errno),
            $"the oracle could not read control 0x{controlId:X8} on '{devNode}' (errno {errno}). "
            + "Without it this test cannot establish ground truth independently of the mapping it "
            + "is checking.");

        return value;
    }

    /// <summary>Reads a control's raw kernel value without asserting.</summary>
    internal static unsafe bool TryRead(string devNode, uint controlId, out int value, out int errno)
    {
        value = 0;

        using var fd = TryOpen(devNode, out errno);
        if (fd is null)
            return false;

        var control = new V4l2Interop.V4l2Control { Id = controlId };
        if (V4l2Interop.IoctlRetry(fd, V4l2Interop.VIDIOC_G_CTRL, &control) < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return false;
        }

        value = control.Value;
        return true;
    }

    /// <summary>Writes a control's raw kernel value without asserting.</summary>
    internal static unsafe bool TryWrite(string devNode, uint controlId, int value)
    {
        using var fd = TryOpen(devNode, out _);
        if (fd is null)
            return false;

        var control = new V4l2Interop.V4l2Control { Id = controlId, Value = value };
        return V4l2Interop.IoctlRetry(fd, V4l2Interop.VIDIOC_S_CTRL, &control) >= 0;
    }

    /// <summary>Whether the device exposes a control at all.</summary>
    /// <remarks>
    /// Used by fixture discovery so a camera missing a control the tests need is rejected as a
    /// fixture, rather than selected and then failing mid-test as though the mapping were wrong
    /// (#280 review turn 1).
    /// </remarks>
    internal static bool Supports(string devNode, uint controlId) =>
        TryRead(devNode, controlId, out _, out _);

    /// <summary>Describes a control — its type and range — without asserting.</summary>
    internal static unsafe bool TryQueryControl(
        string devNode, uint controlId, out V4l2Interop.V4l2QueryCtrl query)
    {
        query = default;

        using var fd = TryOpen(devNode, out _);
        if (fd is null)
            return false;

        var q = new V4l2Interop.V4l2QueryCtrl { Id = controlId };
        if (V4l2Interop.IoctlRetry(fd, V4l2Interop.VIDIOC_QUERYCTRL, &q) < 0)
            return false;

        query = q;
        return true;
    }

    /// <summary>Whether a menu control advertises one particular entry.</summary>
    /// <remarks>
    /// A device need not offer every entry of a standard menu — <c>uvcvideo</c> builds the mask
    /// from the camera's <c>bmAutoExposureMode</c> bitmap — which is the whole subject of #275.
    /// Fixture discovery uses this so a camera that cannot reach the states these tests drive is
    /// rejected as unsuitable, rather than selected and then failing as though Periphery were
    /// wrong (#280 review turn 3).
    /// </remarks>
    internal static unsafe bool AdvertisesMenuEntry(string devNode, uint controlId, int index)
    {
        if (index < 0)
            return false;

        using var fd = TryOpen(devNode, out _);
        if (fd is null)
            return false;

        var menu = new V4l2Interop.V4l2QueryMenu { Id = controlId, Index = (uint)index };
        return V4l2Interop.IoctlRetry(fd, V4l2Interop.VIDIOC_QUERYMENU, &menu) >= 0;
    }
}
