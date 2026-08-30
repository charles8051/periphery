using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Periphery.Camera.Linux;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Which descriptor values <see cref="V4l2FileDescriptor"/> considers valid (#256, #273).
/// </summary>
/// <remarks>
/// Pure type-shape assertions, so they run on any OS — no device, no Linux. They exist because
/// this exact property was misread twice during review, in both directions, and an argument
/// that has to be re-made from measurement every turn should be a test instead.
/// </remarks>
public class V4l2FileDescriptorTests
{
    [Fact]
    public void OnlyMinusOneIsInvalid_BecauseFdZeroIsALegalDescriptor()
    {
        // On Unix 0 is an ordinary file descriptor. open() returns it whenever it is the
        // lowest free number, which is what happens in a process whose stdin is closed — a
        // service. Treating it as invalid would make a working camera report "Device not
        // open" in exactly the deployment this library is aimed at.
#pragma warning disable CA1416 // type shape only; nothing here touches the platform interop
        Assert.Equal(typeof(SafeHandleMinusOneIsInvalid), typeof(V4l2FileDescriptor).BaseType);
#pragma warning restore CA1416
    }

    [Fact]
    public void SafeFileHandleIsNotUsable_ForThisReason()
    {
        // The BCL's own fd wrapper derives from the Zero-or-MinusOne base, so it reports fd 0
        // as invalid. That is the whole reason this backend carries its own type rather than
        // reusing SafeFileHandle, and it is worth pinning: "just use SafeFileHandle" is a
        // reasonable-sounding simplification that would reintroduce the bug above.
        Assert.Equal(typeof(SafeHandleZeroOrMinusOneIsInvalid), typeof(SafeFileHandle).BaseType);

        // And the base this backend does use treats 0 as valid.
        using var overZero = new MinusOneProbe(0);
        using var overMinusOne = new MinusOneProbe(-1);

        Assert.False(overZero.IsInvalid);
        Assert.True(overMinusOne.IsInvalid);
    }

    /// <summary>
    /// Stands in for <see cref="V4l2FileDescriptor"/> so the validity semantics can be
    /// exercised without owning a real descriptor — constructing the real type over fd 0 and
    /// letting it release would close the test process's stdin.
    /// </summary>
    private sealed class MinusOneProbe : SafeHandleMinusOneIsInvalid
    {
        public MinusOneProbe(int fd) : base(ownsHandle: false) => SetHandle((nint)fd);

        protected override bool ReleaseHandle() => true;
    }
}
