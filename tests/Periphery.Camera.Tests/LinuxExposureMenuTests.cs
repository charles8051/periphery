using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Periphery;
using Periphery.Camera;
using Periphery.Camera.Linux;
using Xunit;
using Xunit.Abstractions;

namespace Periphery.Camera.Tests;

/// <summary>
/// The UVC camera-class controls, checked against the kernel rather than against Periphery (#255).
/// </summary>
/// <remarks>
/// These need the physical webcam on the Linux device rig: it is the only fixture with camera-class
/// (<c>0x009a</c>) controls. v4l2loopback has none, and vivid's companions are all booleans, which
/// never exercise the menu path.
/// <para>
/// Every assertion about device state here goes through <see cref="RawV4l2"/>. The previous
/// version of these tests asserted Periphery against Periphery — write a mode, read it back, check
/// they agree — which a self-consistent but wrong mapping satisfies. See <see cref="RawV4l2"/> for
/// the full account of why that passed for the wrong reason (#274).
/// </para>
/// </remarks>
[Collection(RigFixtures.Name)]
// Keeps Category=Integration so every existing `Category!=Integration` exclusion — build.yml,
// publish.yml x2, linux-ci's non-device job — keeps excluding these untouched. The second key is
// what the rig job filters on, so CI never depends on a webcam being plugged in. Adding a NEW
// Category value instead would have been included by all four of those filters and started running
// webcam tests on Windows and macOS runners.
[Trait(RigTraits.Fixture, RigTraits.PhysicalWebcam)]
public class LinuxExposureMenuTests
{
    private readonly ITestOutputHelper _output;

    public LinuxExposureMenuTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1";

    private const int UvcVendorId = 0x3443;
    private const int UvcProductId = 0x60bb;

    /// <summary>
    /// Every raw control these tests touch, so discovery can reject a camera that lacks one.
    /// </summary>
    /// <remarks>
    /// A UVC camera may expose exposure without white balance. Selecting such a camera and then
    /// failing inside the white-balance test would produce a fixture-capability failure wearing
    /// the costume of a mapping failure — the fixture must be rejected up front instead
    /// (#280 review turn 1).
    /// </remarks>
    private static readonly (uint Id, string Name)[] RequiredControls =
    [
        (V4l2Interop.V4L2_CID_EXPOSURE_AUTO, "auto_exposure"),
        (V4l2Interop.V4L2_CID_EXPOSURE_ABSOLUTE, "exposure_time_absolute"),
        (V4l2Interop.V4L2_CID_AUTO_WHITE_BALANCE, "white_balance_automatic"),
        (V4l2Interop.V4L2_CID_WHITE_BALANCE_TEMPERATURE, "white_balance_temperature"),
    ];

    /// <summary>A UVC camera plus the node the oracle reads it through.</summary>
    private sealed record Fixture(DeviceInfo Info, string DevNode);

    private static async Task<Fixture> FindUvcCameraAsync()
    {
        // Guarded here rather than in each test: this helper opens candidate devices itself
        // while probing, so it is the first thing that would touch a leaked fake (#276).
        RigGuard.RequireRealBackend();

        var cameras = await Devices.Enumerate().OfCategory(DeviceCategory.Camera).ToListAsync();
        var rejected = new List<string>();

        // By capability, not by position. One UVC camera publishes more than one /dev/video node
        // and only one of them carries the controls, so "the first match on VID/PID" picked the
        // wrong node on this very device.
        foreach (var candidate in cameras.Where(c =>
                     c.VendorId?.Value == UvcVendorId && c.ProductId?.Value == UvcProductId))
        {
            string devNode;
            try
            {
                await using var probe = await CameraDevice.OpenAsync(candidate);
                var kinds = (await probe.GetControlsAsync()).Select(c => c.Kind).ToHashSet();

                if (!kinds.Contains(CameraControlKind.Exposure)
                    || !kinds.Contains(CameraControlKind.WhiteBalance))
                {
                    rejected.Add($"{candidate.Id.Value}: no Exposure and/or WhiteBalance control");
                    continue;
                }

                devNode = V4l2CameraBackend.ResolveDevNode(candidate.Id.Value);
            }
            catch (CameraException)
            {
                // Not the capture node, or not openable right now; keep looking.
                continue;
            }

            var unmet = UnmetRequirements(devNode);
            if (unmet.Count > 0)
            {
                rejected.Add($"{devNode}: {string.Join(", ", unmet)}");
                continue;
            }

            return new Fixture(candidate, devNode);
        }

        Assert.Fail(
            $"No UVC webcam {UvcVendorId:x4}:{UvcProductId:x4} exposing every control these tests "
            + $"need ({string.Join(", ", RequiredControls.Select(c => c.Name))}). It is passed "
            + "through to the rig VM by `qm set $VMID -usb0 host=3443:60bb`; check `lsusb | grep 3443` "
            + "in the VM and `qm config $VMID | grep usb0` on the hypervisor. It has failed to "
            + "enumerate on a marginal front-panel port before — reseat in a rear USB 3 port and "
            + "check dmesg for "
            + "`cannot enable`. Candidates rejected: "
            + (rejected.Count > 0 ? string.Join("; ", rejected) : "none matched the VID/PID"));
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// What this camera must be able to do before it is a usable fixture — capabilities only.
    /// </summary>
    /// <remarks>
    /// The line these checks deliberately do not cross: they assert what the device
    /// <b>advertises</b>, never how it <b>behaves</b>. "The exposure menu offers entry 0" is a
    /// static property readable with <c>VIDIOC_QUERYMENU</c> and independent of Periphery. "Reset
    /// lands on entry 0" is the behaviour the tests exist to check — validating that here would
    /// make the tests vacuous and turn a genuine mapping bug into a fixture rejection, which is
    /// the silently-not-testing failure this whole rewrite is about (#280 review turn 3).
    /// <para>
    /// Requiring menu entries 0 and 1 also documents the honest limitation: a camera whose menu
    /// omits AUTO — the #275 subset case — is rejected as unsuitable rather than quietly producing
    /// a red that looks like Periphery's fault.
    /// </para>
    /// </remarks>
    private static List<string> UnmetRequirements(string devNode)
    {
        var unmet = new List<string>();

        foreach (var (id, name) in RequiredControls)
        {
            if (!RawV4l2.Supports(devNode, id))
                unmet.Add($"no {name}");
        }

        if (!RawV4l2.TryQueryControl(devNode, V4l2Interop.V4L2_CID_EXPOSURE_AUTO, out var exposure))
        {
            unmet.Add("auto_exposure could not be described");
        }
        else if (exposure.Type != V4l2Interop.V4L2_CTRL_TYPE_MENU)
        {
            unmet.Add($"auto_exposure is control type {exposure.Type}, not a menu — these tests "
                      + "exist to check the menu rule");
        }
        else
        {
            foreach (var (entry, name) in new[]
                     {
                         (V4l2Interop.V4L2_EXPOSURE_AUTO_MODE, "AUTO (0)"),
                         (V4l2Interop.V4L2_EXPOSURE_MANUAL, "MANUAL (1)"),
                     })
            {
                if (!RawV4l2.AdvertisesMenuEntry(devNode, V4l2Interop.V4L2_CID_EXPOSURE_AUTO, entry))
                    unmet.Add($"auto_exposure does not advertise {name}");
            }
        }

        if (!RawV4l2.TryQueryControl(
                devNode, V4l2Interop.V4L2_CID_AUTO_WHITE_BALANCE, out var whiteBalance))
        {
            unmet.Add("white_balance_automatic could not be described");
        }
        else if (whiteBalance.Type != V4l2Interop.V4L2_CTRL_TYPE_BOOLEAN)
        {
            unmet.Add($"white_balance_automatic is control type {whiteBalance.Type}, not a boolean "
                      + "— the contrast with the exposure menu is the point of that test");
        }

        return unmet;
    }

    /// <summary>The raw pair that describes a control: its auto companion and its value.</summary>
    private sealed record RawState(int Companion, int Value);

    private static RawState CaptureRaw(string devNode, uint companionId, uint valueId) =>
        new(RawV4l2.Read(devNode, companionId), RawV4l2.Read(devNode, valueId));

    /// <summary>
    /// Runs <paramref name="body"/> and puts the control back exactly as it was found.
    /// </summary>
    /// <remarks>
    /// <b>Restores, rather than resetting.</b> The helper this replaces called
    /// <c>ResetControlAsync</c>, which writes <c>v4l2_queryctrl.default_value</c> — the driver's
    /// default, not the value the test found. That is not a restore, and it was not a harmless
    /// inaccuracy: it permanently moved this rig's camera (<c>exposure_time_absolute</c> 5000 to
    /// 100, <c>white_balance_temperature</c> 4680 to 4650) while the test reported that it had
    /// tidied up after itself (#274).
    /// <para>
    /// Restoration goes through <see cref="RawV4l2"/> rather than the camera API on purpose:
    /// cleanup must not depend on the mapping the test is probing, or a mapping bug corrupts the
    /// fixture and hides itself.
    /// </para>
    /// <para>
    /// <b>A failed restore fails the test.</b> This is a shared physical camera; logging alone
    /// would let a run finish green having left it altered for every later test and for whoever
    /// uses the rig next. But cleanup must never <em>mask</em> a failure either, so the rule is
    /// asymmetric: if the body already failed, restoration problems are written to the test output
    /// and the original exception is left to propagate; only when the body succeeded does a failed
    /// restore turn the test red (#280 review turn 1).
    /// </para>
    /// </remarks>
    private async Task WithRestoredControlAsync(
        string devNode, uint companionId, uint valueId, int manualValue, Func<Task> body)
    {
        var original = CaptureRaw(devNode, companionId, valueId);
        Exception? bodyFailure = null;

        try
        {
            await body();
        }
        catch (Exception ex)
        {
            bodyFailure = ex;
            throw;
        }
        finally
        {
            var problems = TryRestoreRaw(devNode, companionId, valueId, original, manualValue);

            if (problems.Count > 0)
            {
                string detail = string.Join("; ", problems);

                if (bodyFailure is null)
                    Assert.Fail(
                        "the test body succeeded but the camera was not put back as it was found, "
                        + "so this run has altered a shared fixture: " + detail);

                _output.WriteLine("cleanup also failed, while the test was already failing: " + detail);
            }
        }
    }

    /// <summary>
    /// Replays the captured raw state. Never throws; returns what went wrong.
    /// </summary>
    /// <remarks>
    /// Manual is selected first because a value control is inactive while its companion holds
    /// automatic — writing the value then would be refused.
    /// </remarks>
    private static List<string> TryRestoreRaw(
        string devNode, uint companionId, uint valueId, RawState original, int manualValue)
    {
        var problems = new List<string>();

        if (!RawV4l2.TryWrite(devNode, companionId, manualValue))
            problems.Add($"could not select manual ({manualValue}) on 0x{companionId:X8}");

        if (!RawV4l2.TryWrite(devNode, valueId, original.Value))
            problems.Add($"could not put 0x{valueId:X8} back to {original.Value}");

        if (!RawV4l2.TryWrite(devNode, companionId, original.Companion))
            problems.Add($"could not put 0x{companionId:X8} back to {original.Companion}");

        // Verified, not assumed — and with the non-asserting read, since this runs in cleanup.
        if (!RawV4l2.TryRead(devNode, companionId, out int companion, out int companionErrno))
            problems.Add($"could not read back 0x{companionId:X8} (errno {companionErrno})");
        else if (companion != original.Companion)
            problems.Add($"0x{companionId:X8} left at {companion}, found at {original.Companion}");

        if (!RawV4l2.TryRead(devNode, valueId, out int value, out int valueErrno))
            problems.Add($"could not read back 0x{valueId:X8} (errno {valueErrno})");
        else if (value != original.Value)
            problems.Add($"0x{valueId:X8} left at {value}, found at {original.Value}");

        return problems;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExposureUnderAutomatic_SitsAtRawMenuZero_WhilePeripheryReportsAutomatic()
    {
        // THE claim #255 was filed to check, and the one the old test could not actually make.
        //
        // V4L2_CID_EXPOSURE_AUTO is a menu whose sense is inverted relative to every boolean
        // companion: AUTO is 0 and MANUAL is 1, whereas AUTOGAIN and AUTO_WHITE_BALANCE use 1 for
        // automatic. Read as a boolean, 0 is "false" and every auto-exposure camera reports as
        // Manual -- a wrong answer that looks entirely plausible.
        //
        // The two assertions below are deliberately of DIFFERENT KINDS. The first is a fact about
        // the device, obtained without Periphery. The second is Periphery's claim about that same
        // device. Only holding them side by side pins the mapping: break InterpretAutoValue to the
        // boolean rule and the raw value is still 0 while Periphery says Manual, so the second
        // assertion fails against the first's evidence -- rather than the old arrangement, where
        // a wrong mapping was only ever checked against itself.
        if (!Enabled) return;

        var fixture = await FindUvcCameraAsync();

        await WithRestoredControlAsync(
            fixture.DevNode,
            V4l2Interop.V4L2_CID_EXPOSURE_AUTO,
            V4l2Interop.V4L2_CID_EXPOSURE_ABSOLUTE,
            V4l2Interop.V4L2_EXPOSURE_MANUAL,
            async () =>
            {
                await using var device = await CameraDevice.OpenAsync(fixture.Info);
                await device.ResetControlAsync(CameraControlKind.Exposure);

                Assert.Equal(V4l2Interop.V4L2_EXPOSURE_AUTO_MODE,
                    RawV4l2.Read(fixture.DevNode, V4l2Interop.V4L2_CID_EXPOSURE_AUTO));

                var state = await device.GetControlAsync(CameraControlKind.Exposure);
                Assert.NotNull(state);
                Assert.Equal(CameraControlMode.Automatic, state!.Mode);
            });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SettingExposure_MovesTheRawMenuToManual_AndPeripheryAgrees()
    {
        // The other direction, same discipline: the kernel says 1 (MANUAL) and Periphery says
        // Manual. Writing a value is what should take the control off automatic, so this also
        // pins that SetControlAsync drives the companion rather than only the value.
        if (!Enabled) return;

        var fixture = await FindUvcCameraAsync();

        await WithRestoredControlAsync(
            fixture.DevNode,
            V4l2Interop.V4L2_CID_EXPOSURE_AUTO,
            V4l2Interop.V4L2_CID_EXPOSURE_ABSOLUTE,
            V4l2Interop.V4L2_EXPOSURE_MANUAL,
            async () =>
            {
                await using var device = await CameraDevice.OpenAsync(fixture.Info);
                var range = (await device.GetControlsAsync())
                    .First(c => c.Kind == CameraControlKind.Exposure);
                double target = Math.Round(((range.MinValue ?? 0) + (range.MaxValue ?? 1000)) / 2);

                await device.SetControlAsync(CameraControlKind.Exposure, target);

                Assert.Equal(V4l2Interop.V4L2_EXPOSURE_MANUAL,
                    RawV4l2.Read(fixture.DevNode, V4l2Interop.V4L2_CID_EXPOSURE_AUTO));

                var state = await device.GetControlAsync(CameraControlKind.Exposure);
                Assert.NotNull(state);
                Assert.Equal(CameraControlMode.Manual, state!.Mode);
                Assert.Equal(target, state.Value);
            });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhiteBalanceAutomatic_SitsAtRawOne_TheOppositeSenseToTheExposureMenu()
    {
        // The contrast that makes the menu rule load-bearing, on the SAME device so it cannot be
        // explained away by hardware differences: white balance is a boolean companion where 1
        // means automatic, while exposure's menu uses 0 for automatic.
        //
        // The old version of this test asserted only that Periphery reported Automatic, so it
        // PASSED with the exposure rule wrongly applied to booleans -- it never looked at which
        // number the kernel actually held. Asserting the literal 1 is what makes this a control
        // on the exposure tests rather than a companion to them.
        if (!Enabled) return;

        var fixture = await FindUvcCameraAsync();

        await WithRestoredControlAsync(
            fixture.DevNode,
            V4l2Interop.V4L2_CID_AUTO_WHITE_BALANCE,
            V4l2Interop.V4L2_CID_WHITE_BALANCE_TEMPERATURE,
            0,
            async () =>
            {
                await using var device = await CameraDevice.OpenAsync(fixture.Info);
                await device.ResetControlAsync(CameraControlKind.WhiteBalance);

                Assert.Equal(1,
                    RawV4l2.Read(fixture.DevNode, V4l2Interop.V4L2_CID_AUTO_WHITE_BALANCE));

                var state = await device.GetControlAsync(CameraControlKind.WhiteBalance);
                Assert.NotNull(state);
                Assert.Equal(CameraControlMode.Automatic, state!.Mode);
            });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResettingExposureFromManual_ReachesAutomatic_WhichRequiresQueryingTheMenu()
    {
        // The end-to-end check that VIDIOC_QUERYMENU works (#275), arranged so a BROKEN query
        // cannot pass. EnforceCompanionMode asks SupportsMenuEntry before writing; if the ioctl
        // request code or the packed struct were wrong, every query would answer EINVAL, every
        // candidate would be skipped and no write would happen. Starting from MANUAL is what
        // makes that fatal -- the read-back would report Manual and the reset would throw.
        // Starting from an already automatic camera would mask exactly that.
        if (!Enabled) return;

        var fixture = await FindUvcCameraAsync();

        await WithRestoredControlAsync(
            fixture.DevNode,
            V4l2Interop.V4L2_CID_EXPOSURE_AUTO,
            V4l2Interop.V4L2_CID_EXPOSURE_ABSOLUTE,
            V4l2Interop.V4L2_EXPOSURE_MANUAL,
            async () =>
            {
                await using var device = await CameraDevice.OpenAsync(fixture.Info);
                var range = (await device.GetControlsAsync())
                    .First(c => c.Kind == CameraControlKind.Exposure);

                await device.SetControlAsync(CameraControlKind.Exposure,
                    Math.Round(((range.MinValue ?? 0) + (range.MaxValue ?? 1000)) / 2));
                Assert.Equal(V4l2Interop.V4L2_EXPOSURE_MANUAL,
                    RawV4l2.Read(fixture.DevNode, V4l2Interop.V4L2_CID_EXPOSURE_AUTO));

                await device.ResetControlAsync(CameraControlKind.Exposure);

                Assert.Equal(V4l2Interop.V4L2_EXPOSURE_AUTO_MODE,
                    RawV4l2.Read(fixture.DevNode, V4l2Interop.V4L2_CID_EXPOSURE_AUTO));
            });
    }
}
