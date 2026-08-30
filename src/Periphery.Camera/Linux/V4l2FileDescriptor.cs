// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Periphery.Camera.Linux;

/// <summary>
/// An owned <c>/dev/videoN</c> file descriptor.
/// </summary>
/// <remarks>
/// A raw <c>int</c> fd cannot be used safely across a teardown, because on Linux a closed fd
/// number is <b>immediately available for reuse</b> by any thread in the process opening any
/// file. A backend that checked "am I open?" and then issued an <c>ioctl</c> on the number it
/// had read could land that <c>ioctl</c> on a descriptor now belonging to something else
/// entirely — and the failure would not be an exception. <c>VIDIOC_S_CTRL</c> against a
/// non-V4L2 fd returns <c>ENOTTY</c>, which reads exactly like a device that declined the
/// control (issue #256).
/// <para>
/// Passing this type to a P/Invoke instead makes the runtime ref-count the handle for the
/// duration of the call, so <c>Dispose</c> cannot close the fd underneath an
/// in-flight <c>ioctl</c>, and a call that arrives after teardown throws
/// <see cref="ObjectDisposedException"/> rather than silently addressing a stranger's
/// descriptor. That converts a silent wrong-device call into a loud, correct one — and covers
/// any call site added later for free, which guarding the three known ones would not.
/// </para>
/// <para>
/// The <c>poll()</c> path cannot take a handle — <c>struct pollfd</c> carries a bare
/// <c>int</c> — so it ref-counts explicitly via <see cref="SafeHandle.DangerousAddRef"/>. That
/// is the one place the guarantee is hand-maintained rather than given by marshalling.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class V4l2FileDescriptor : SafeHandleMinusOneIsInvalid
{
    private V4l2FileDescriptor() : base(ownsHandle: true) { }

    internal V4l2FileDescriptor(int fd) : base(ownsHandle: true) => SetHandle(fd);

    /// <summary>The raw descriptor, for the <c>poll()</c> path only. Callers must hold a
    /// ref-count (<see cref="SafeHandle.DangerousAddRef"/>) across the use.</summary>
    internal int UnsafeFd => (int)handle;

    protected override bool ReleaseHandle() => V4l2Interop.Close((int)handle) == 0;
}
