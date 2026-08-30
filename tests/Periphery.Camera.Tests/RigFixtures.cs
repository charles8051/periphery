using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// The collection every device-backed test on the rig belongs to.
/// </summary>
/// <remarks>
/// <b>Do not remove <c>DisableParallelization</c>.</b> It looks redundant — xUnit already runs
/// tests within one class sequentially — and it is not. It does two jobs:
/// <list type="number">
/// <item>
/// Serialises writes to <b>shared physical hardware</b>. The rig's UVC camera is one device
/// visible to every test and to whoever uses the rig next, and capture-mutate-assert-restore is
/// only safe if it runs alone.
/// </item>
/// <item>
/// Keeps these tests away from the <b>process-global fake backend</b> (issue #276).
/// <c>CameraDevice.BackendFactory</c> is a plain <c>static</c>, installed for the lifetime of the
/// parallelizable <c>"Camera"</c> collection's fixture. A device test running concurrently with
/// that collection resolves <c>CameraDevice.OpenAsync</c> to an <c>InMemoryCameraBackend</c> and
/// passes <em>having tested nothing</em> — the precise failure the rig's hard-fail-never-skip
/// policy exists to prevent, one level below where that policy operates. A non-parallelizable
/// collection runs alone, so the factory is never installed while these run.
/// </item>
/// </list>
/// Job 2 is invisible at the call site, which is why <see cref="RigGuard"/> asserts it rather
/// than leaving it to this comment.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RigFixtures
{
    public const string Name = "Linux device-rig fixtures";
}

/// <summary>
/// Trait keys and values that decide which rig tests a given runner may execute.
/// </summary>
/// <remarks>
/// <b>Why a second trait key rather than another <c>Category</c> value.</b> Four workflow steps
/// select the non-device suite with <c>Category!=Integration</c> (build.yml, publish.yml twice, and
/// linux-ci's own build job). A new <c>Category</c> value would be <em>included</em> by every one of
/// them, so tests needing a physical webcam would start running on Windows and macOS runners that
/// have never had one. Keeping <c>Category=Integration</c> leaves all four correct and untouched,
/// and the rig job — the only place that opts device tests <em>in</em> — excludes the physical
/// fixture explicitly.
/// <para>
/// The rule this encodes: <b>CI must never depend on hardware a person has to plug in.</b> Every
/// other rig fixture is synthesized (v4l2loopback, vivid, uhid, QEMU-emulated USB HID) and so is
/// reproducible on demand; the UVC webcam is not, and a run must not go red because someone
/// unplugged it.
/// </para>
/// </remarks>
internal static class RigTraits
{
    internal const string Fixture = "Fixture";

    /// <summary>Needs the physical UVC webcam. Excluded from every CI path.</summary>
    /// <remarks>
    /// Enforced by two different things, deliberately unequal. The code-level half —
    /// <c>EveryPhysicalFixtureClass_AlsoCarriesTheIntegrationCategory</c> — is a real test,
    /// because dropping <c>Category=Integration</c> would silently start running these on Windows
    /// and macOS runners. The workflow half is a comment on the rig job's filter and nothing more:
    /// removing <c>Fixture!=PhysicalWebcam</c> there fails <em>loudly</em>, in a manually
    /// dispatched job, with a message naming the webcam and how to reattach it. Guarding a loud
    /// failure was not worth a repo-layout-coupled YAML lint (#281 review turn 3).
    /// </remarks>
    internal const string PhysicalWebcam = "PhysicalWebcam";
}

/// <summary>
/// Checks that a device test is really talking to a device.
/// </summary>
internal static class RigGuard
{
    /// <summary>
    /// Fails if the in-memory fake backend is installed.
    /// </summary>
    /// <remarks>
    /// Cheap insurance against issue #276. Without it, a leak of the process-global factory turns
    /// every device test into a green run against <c>InMemoryCameraBackend</c> — which advertises
    /// plausible formats and emits non-zero frames, so nothing downstream notices.
    /// </remarks>
    internal static void RequireRealBackend()
    {
        Assert.True(CameraDevice.BackendFactory is null,
            "the in-memory camera backend is installed, so this device test would run against a "
            + "fake and pass without touching hardware (#276). CameraDevice.BackendFactory is a "
            + "process-global static owned by the parallelizable \"Camera\" collection fixture; a "
            + "device test must be in the non-parallel RigFixtures collection so the two never "
            + "overlap.");
    }
}
