using OpenCvSharp;

namespace Periphery.Camera.OpenCvSharp.Interop.Tests;

/// <summary>
/// Whether <c>OpenCvSharpExtern</c> can be loaded in this process, and the
/// <c>[Fact]</c> / <c>[Theory]</c> variants that skip when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Category=Integration</c> trait on these tests keeps them out of the
/// default CI filter, which is what the Windows, Linux and macOS build legs run.
/// It is not enough on its own: the macOS device rig's job runs
/// <c>--filter "Category=Integration"</c> with no further exclusion, and macOS
/// has no current first-party native package. A hard
/// <c>DllNotFoundException</c> there would report a missing payload as a broken
/// conversion.
/// </para>
/// <para>
/// So the gate is a skip, and it is a skip on the real condition — can this
/// process call OpenCV — rather than on an operating-system name. A macOS rig
/// that later grows a working payload starts running these tests without anyone
/// editing a platform list.
/// </para>
/// </remarks>
internal static class OpenCvNative
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    /// <summary>Null when OpenCV is callable, otherwise the reason to skip.</summary>
    internal static string? SkipReason => Unavailable.Value;

    private static string? Probe()
    {
        try
        {
            // The smallest call that has to cross into native code. GetVersionString
            // would do as well; allocating and freeing a 1x1 Mat also proves the
            // allocator is wired up, which is what every test here then leans on.
            using var probe = new Mat(1, 1, MatType.CV_8UC1);
            return probe.Empty() ? "OpenCV loaded but could not allocate a 1x1 Mat." : null;
        }
        catch (Exception ex)
        {
            return $"OpenCvSharpExtern is not loadable here ({ex.GetType().Name}): {ex.Message}. "
                + "Install an OpenCvSharp4.runtime.* package for this platform.";
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips when OpenCV's native
/// library cannot be loaded.</summary>
public sealed class OpenCvFactAttribute : FactAttribute
{
    public OpenCvFactAttribute()
    {
        if (OpenCvNative.SkipReason is { } reason)
            Skip = reason;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when OpenCV's native
/// library cannot be loaded.</summary>
public sealed class OpenCvTheoryAttribute : TheoryAttribute
{
    public OpenCvTheoryAttribute()
    {
        if (OpenCvNative.SkipReason is { } reason)
            Skip = reason;
    }
}
